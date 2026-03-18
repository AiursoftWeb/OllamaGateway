using System;
using Aiursoft.GptClient.Abstractions;
using Newtonsoft.Json;

class Program {
    static void Main() {
        var msg = new OpenAIChatMessage { Role = "user", Content = "test" };
        var props = typeof(OpenAIChatMessage).GetProperties();
        foreach(var p in props) Console.WriteLine(p.Name);
    }
}
