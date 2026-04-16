using System.Net;
using Aiursoft.OllamaGateway.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.OllamaGateway.Tests.IntegrationTests;

[TestClass]
public class ApiKeyExpirationTests : TestBase
{
    private static readonly HttpClient CleanHttp = new HttpClient();

    [TestMethod]
    public async Task TestExpiredApiKey()
    {
        // 1. Setup Data with an expired API Key
        string apiKeyStr = "expired-token-123";
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var user = await db.Users.FirstAsync();
            db.ApiKeys.Add(new ApiKey 
            { 
                Name = "Expired Key", 
                Key = apiKeyStr, 
                UserId = user.Id,
                ExpirationTime = DateTime.UtcNow.AddMinutes(-1) // Already expired
            });
            await db.SaveChangesAsync();
        }

        // 2. Request with Expired API Key
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/tags");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKeyStr);
        var response = await Http.SendAsync(request);
        
        // Should return 401 Unauthorized because the key is expired
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task TestValidApiKey()
    {
        // 1. Setup Data with a valid API Key
        string apiKeyStr = "valid-token-123";
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var user = await db.Users.FirstAsync();
            db.ApiKeys.Add(new ApiKey 
            { 
                Name = "Valid Key", 
                Key = apiKeyStr, 
                UserId = user.Id,
                ExpirationTime = DateTime.UtcNow.AddDays(1) // Valid
            });
            await db.SaveChangesAsync();
        }

        // 2. Request with Valid API Key
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/tags");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKeyStr);
        var response = await Http.SendAsync(request);
        
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [TestMethod]
    public async Task TestApiKeyExpirationWorkflow()
    {
        var (email, password) = await RegisterAndLoginAsync(); 
        await GrantPermissionToUser(email, Authorization.AppPermissionNames.CanManageApiKeys);

        // RE-LOGIN to refresh claims in cookie
        await PostForm("/Account/Login", new Dictionary<string, string>
        {
            { "EmailOrUserName", email },
            { "Password", password }
        });

        // 1. Create API Key with 1 day expiration
        var createResponse = await PostForm("/ApiKeys/Create", new Dictionary<string, string>
        {
            { "Name", "One Day Key" },
            { "ExpirationDays", "1" }
        });
        AssertRedirect(createResponse, "/ApiKeys", exact: false);

        // 2. Verify key exists and check expiration
        var db = GetService<TemplateDbContext>();
        var key = await db.ApiKeys.OrderByDescending(k => k.Id).FirstAsync();
        Assert.IsTrue(key.ExpirationTime > DateTime.UtcNow);
        Assert.IsTrue(key.ExpirationTime < DateTime.UtcNow.AddHours(25));

        // 3. Edit API Key to expire it
        var expiredTime = DateTime.UtcNow.AddDays(-1);
        var editResponse = await PostForm("/ApiKeys/Edit", new Dictionary<string, string>
        {
            { "Id", key.Id.ToString() },
            { "Name", "Manually Expired" },
            { "ExpirationTime", expiredTime.ToString("yyyy-MM-ddTHH:mm") }
        });
        AssertRedirect(editResponse, "/ApiKeys", exact: false);

        // 4. Verify in DB
        var updatedKey = await db.ApiKeys.AsNoTracking().FirstOrDefaultAsync(k => k.Id == key.Id);
        Assert.AreEqual("Manually Expired", updatedKey!.Name);
        // Compare with some tolerance due to string conversion in form post
        Assert.IsTrue(Math.Abs((updatedKey.ExpirationTime - expiredTime).TotalMinutes) < 2);

        // 5. Verify it fails authentication
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(Http.BaseAddress!, "/api/tags"));
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", updatedKey.Key);
        var response = await CleanHttp.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
