# API Translation Matrix

## Overview

This project translates between 3 AI API dialects: **Ollama**, **OpenAI**, and **Anthropic**. With 2 backend types (Ollama, OpenAI), there are 6 active translation paths out of a possible 9. Anthropic-native backend does not exist yet (`ProviderType` enum has no `Anthropic` value).

Total: **~3030 lines** across 3 controllers.

## 3×3 Matrix

| # | Frontend | Backend | Code Location | ~Lines |
|---|----------|---------|---------------|--------|
| 1 | Ollama | Ollama | `ProxyController:420-557` | ~140 |
| 2 | Ollama | OpenAI | `ProxyController:204-415` | ~210 |
| 3 | OpenAI | OpenAI | `OpenAIController:155-306` | ~150 |
| 4 | OpenAI | Ollama | `OpenAIController:315-691` | ~375 |
| 5 | Anthropic | OpenAI | `AnthropicController:218-840` | ~620 |
| 6 | Anthropic | Ollama | `AnthropicController:218-840` | ~620 |
| 7 | Ollama | Anthropic | — | N/A |
| 8 | OpenAI | Anthropic | — | N/A |
| 9 | Anthropic | Anthropic | — | N/A |

Paths 5 & 6 share the same method, branching on `isOllamaDirect`.

---

## Path 1: Ollama → Ollama (Passthrough + Model Masking)

**~140 lines** (`ProxyController:420-557`)

- Request body serialized as-is to `/api/chat`, only `model` replaced with `backend.UnderlyingModelName`
- Streaming: read NDJSON line by line, rewrite `model` field to virtual model name, emit unchanged
- Non-streaming: read full JSON, rewrite `model`, emit

### Pitfalls

1. **Serialization framework split**: Inbound uses Newtonsoft.Json (`[FromBody]` model binding), outbound uses System.Text.Json + `SnakeCaseLower`. Newtonsoft doesn't recognize snake_case — every Ollama field needs explicit `[Newtonsoft.Json.JsonProperty("num_ctx")]`. STJ ignores Newtonsoft attributes. Field mappings written twice.
2. **`thinking` vs `think`**: Ollama renamed this field between versions. Code reads both (`:490-491`).
3. **Model name leak**: Must replace the backend's real model name with the virtual model name in every chunk.

---

## Path 2: Ollama → OpenAI (Translation + Streaming Tool Call Accumulation)

**~210 lines** (`ProxyController:204-415`)

- Request: Ollama `content` (plain string) + `images` array → OpenAI `messages[].content` (string). `num_predict` → `max_tokens`.
- Streaming response: OpenAI SSE → Ollama NDJSON. Tool calls accumulated across chunks. Final chunk injects `prompt_eval_count`/`eval_count`.
- Non-streaming: OpenAI JSON → Ollama JSON. Tool call arguments parsed from JSON string to object.

### Pitfalls

1. **Tool call arrives in fragments**: OpenAI streams `index`, `id`, `function.name`, `function.arguments` in separate delta chunks. Must use `Dictionary<int, (string Name, StringBuilder Args)>` to accumulate across chunks. Only emit Ollama-format tool call when `finish_reason` signals completion.
2. **Arguments type conversion**: OpenAI sends `"arguments": "{\"city\": \"Beijing\"}"` (JSON string), Ollama expects `"arguments": {"city": "Beijing"}` (JSON object). Trivial for non-streaming (`JsonNode.Parse`), impossible mid-stream since arguments are incomplete.
3. **Silent parameter drop**: Ollama's `top_k`, `num_ctx`, `repeat_penalty` have no OpenAI equivalent. Dropped without warning.
4. **Usage field mapping**: `prompt_tokens` → `prompt_eval_count`, `completion_tokens` → `eval_count`.

---

## Path 3: OpenAI → OpenAI (Near-Passthrough)

**~150 lines** (`OpenAIController:155-306`)

- Request forwarded as-is to `/v1/chat/completions`, model name replaced, virtual model overrides injected.
- Streaming: SSE forwarded as-is, model name replaced, `reasoning_content` extracted for logging.
- Non-streaming: same.

### Pitfalls

1. **Null content patch**: Ollama's OpenAI-compatible mode rejects `content: null` (though OpenAI spec allows it). Must patch `content: null` → `content: ""` and `role: null` → `role: "user"` before forwarding (`:164-179`).
2. **`reasoning_content` is non-standard**: DeepSeek injects `reasoning_content` into delta chunks in OpenAI-compatible mode. Standard OpenAI doesn't have this field. Must be passed through and logged.

---

## Path 4: OpenAI → Ollama (Full Bidirectional Translation)

**~375 lines** (`OpenAIController:315-691`) — largest single path.

- Request: OpenAI multimodal content (`[{type: "text"}, {type: "image_url"}]`) → Ollama `content` (string) + `images` (base64 array). Tool call arguments string→object. `max_tokens` → `num_predict`. Inject Ollama-only params (`top_k`, `num_ctx`, `thinking`).
- Streaming response: Ollama NDJSON → OpenAI SSE (`chat.completion.chunk`). Generate `id`, `object`, `created`. Tool call object→string. `thinking`/`think` → `reasoning_content`.
- Non-streaming: same, constructing full `chat.completion` object.

### Pitfalls

1. **Content structure mismatch**: OpenAI content can be string or `[{type, text/image_url}]` array. Ollama content is always string + optional `images` string array. Must iterate content array, concatenate text parts, extract base64 from image_url.
2. **Tool call arguments bidirectional conversion**:
   - Request: OpenAI string → Ollama object (`JsonNode.Parse`)
   - Response: Ollama object → OpenAI string (`ToJsonString()`)
   - Fallback to `{}` if arguments are malformed (`:390-391`)
3. **`thinking` field dual naming**: Historical Ollama uses `thinking`, newer uses `think`. Code reads both (`:499-501`).
4. **OpenAI response skeleton construction**: Ollama responses lack `id`, `object`, `created`. Gateway fabricates `chatId` as `"chatcmpl-" + Guid`, `created` as current timestamp.
5. **`finish_reason` inference**: Ollama only gives `done: true` + `done_reason: "length"`. Gateway must track whether tool calls appeared to decide `finish_reason` (`:562`).
6. **Empty chunk filtering**: Ollama sometimes emits empty chunks (done marker but no content). Must check `delta.Count == 0 && !isDone` to skip, otherwise OpenAI clients break.

---

## Paths 5 & 6: Anthropic → OpenAI / Ollama

**~620 lines** (`AnthropicController:218-840`, shared method, branches on `isOllamaDirect`)

### Request Translation (shared)

- Anthropic `system` (string or content block array) → OpenAI `system` message
- Anthropic `content` array (`[{type: "text"}, {type: "tool_use"}, {type: "tool_result"}, {type: "thinking"}]`) → OpenAI `role` + `content` + `tool_calls` + `tool_call_id`
- Anthropic `thinking` block → OpenAI `reasoning_content` or `<think>` tags
- Anthropic `tools[].input_schema` → OpenAI `tools[].function.parameters`

### Ollama Backend Extra Adaptation

- Tool call arguments string→object (`:427-433`)
- Parameters moved to `options` sub-object, `max_tokens` → `num_predict`
- Inject Ollama-only params: `top_k`, `num_ctx`, `thinking`

### Response Translation (shared, streaming)

- Construct Anthropic SSE event sequence: `message_start` → `content_block_start` → `content_block_delta` (repeated) → `content_block_stop` → `message_delta` → `message_stop`
- Ollama NDJSON or OpenAI SSE → Anthropic SSE event stream
- `reasoning_content` / `thinking` → `<think>...</think>` tag wrapping
- Tool call delta → `input_json_delta` event

### Pitfalls

1. **Content is structured arrays**: Anthropic content is `[{type, text/tool_use/tool_result/thinking}]` blocks. Translating to OpenAI's `role + content` model requires full array traversal with type dispatch. `text` → concatenate into content, `tool_use` → construct `tool_calls`, `tool_result` → construct `tool` role message. `tool_result` content itself can be structured arrays (recursive).
2. **System prompt is content block array**: Anthropic system can be string or `[{type: "text"}]` array. `ExtractTextFromBlocks` method (68 lines, `:48-117`) handles recursive text extraction.
3. **Reasoning format incompatibility**: Anthropic native uses `thinking` content block. OpenAI uses `reasoning_content` field. Ollama uses `thinking`/`think` field. DeepSeek uses `<think>` tags. When translating to Anthropic output, must detect and extract `<think>` tags (`:323-339`), wrapping extracted reasoning in `<think>...</think>`. Also handles `<think>` tags spanning multiple stream chunks (`:588-606`).
4. **Content block index management**: Anthropic SSE requires each content block to have a unique index (text=0, tool_use_1=1, tool_use_2=2...). Must emit `content_block_start` before `content_block_delta`. Gateway maintains `activeToolBlocks` HashSet to track which indices have had their start event (`:537, 650`).
5. **`stop_reason` mapping**:
   - Ollama `done_reason: "length"` → `"max_tokens"`
   - OpenAI `finish_reason: "tool_calls"` → `"tool_use"`
   - OpenAI `finish_reason: "stop"` → `"end_turn"`
   - Default: `"end_turn"`
6. **Usage position inconsistency**: Anthropic puts usage in `message_delta` event (only `output_tokens`). OpenAI puts usage in the last chunk's top level. Ollama puts usage in the `done: true` chunk, with different field names (`prompt_eval_count`/`eval_count`).
7. **Newtonsoft vs STJ dual serializer**: Content may arrive as `Newtonsoft.Json.Linq.JToken` (from ASP.NET Core model binding) or `System.Text.Json.JsonNode` (from `JsonSerializer.SerializeToNode`). `ExtractTextFromBlocks` handles both (`:53-84` vs `:86-117`).
8. **Ollama `think` parameter injection**: Anthropic requests have no `think` concept (thinking is a content block), but Ollama backend needs `think: true/false`. Injected from virtual model config `virtualModel.Thinking` (`:460`).

---

## Cross-Cutting Pitfalls Summary

| Pitfall | Affected Paths | Severity |
|---------|---------------|----------|
| Tool call arguments string↔object conversion | 2, 4, 5, 6 | High |
| Streaming tool call cross-chunk accumulation | 2, 4, 5, 6 | High |
| Content structure differences (string vs array vs blocks) | 4, 5, 6 | High |
| Reasoning/thinking format incompatibility | 3, 4, 5, 6 | Medium |
| Silent parameter drop (`top_k`, `num_ctx`, etc.) | 1, 2, 3, 4 | Medium |
| Usage token field names differ per dialect | All 6 | Medium |
| SSE/NDJSON event structure completely different | 2, 4, 5, 6 | High |
| Serialization framework split (Newtonsoft vs STJ) | 1, 5, 6 | Low |
| `finish_reason`/`stop_reason`/`done_reason` mapping | 4, 5, 6 | Medium |
| Empty chunk / null content patching | 3, 4, 6 | Low |
| Model name leak | All 6 | Low |

---

## Missing Paths (Anthropic-native Backend)

`ProviderType` enum has no `Anthropic` value. Adding it would enable 3 more paths:

| # | Frontend | Backend | Effort Estimate |
|---|----------|---------|-----------------|
| 7 | Ollama | Anthropic | Full translation (equivalent to reverse of path 6) |
| 8 | OpenAI | Anthropic | Full translation (equivalent to reverse of path 5) |
| 9 | Anthropic | Anthropic | Trivial (passthrough + model masking) |

This would bring the matrix to 9 paths × 2 (streaming/non-streaming) = 18 code branches.

---

## Why a Canonical Intermediate Format Doesn't Help

The intuitive "encoder-decoder" approach (frontend → canonical IR → backend) fails for three reasons:

1. **The IR is necessarily more complex than any single format.** It must be the *union* of all parameters across all dialects, which means it carries fields that are meaningless for most paths. Translating to a specific backend requires dropping or fabricating parameters — the same logic, just moved to a different layer.

2. **Streaming breaks the IR model.** An IR assumes full parse → transform → emit. With SSE/NDJSON streaming, each chunk lacks complete semantic context. Tool call arguments span multiple chunks. You can't buffer everything because the user needs real-time output. A "streaming IR" just pushes the same N×M complexity down one layer.

3. **No-op paths pay unnecessary overhead.** When frontend format equals backend format (paths 1, 3), an IR round-trip would serialize → deserialize → reserialize, introducing JSON round-trip precision loss and field ordering changes for zero benefit.

The fundamental problem is not architectural — it's that **there is no unified AI API standard.** OpenAI's format became a de facto standard, but every provider patches it differently. Ollama went a completely separate route. No amount of internal abstraction eliminates the N×M combinatorial complexity of the semantic differences.
