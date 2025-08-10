using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

class Program
{
    static async Task Main()
    {
        var exe = @".\WeatherMcpServer\bin\Release\net8.0\WeatherMcpServer.exe";

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        var p = Process.Start(psi)!;

        // keep STDERR as-is (your choice)
        _ = Task.Run(async () => {
            while (!p.StandardError.EndOfStream)
            {
                var line = await p.StandardError.ReadLineAsync();
                //if (line != null) Console.WriteLine("SERVER-ERR: " + line);
            }
        });

        // helper to send and await a specific id on STDOUT
        async Task<JsonObject?> RpcAsync(object payload, int idToWait)
        {
            var json = JsonSerializer.Serialize(payload);
            await p.StandardInput.WriteLineAsync(json);
            await p.StandardInput.FlushAsync();

            while (!p.StandardOutput.EndOfStream)
            {
                var line = await p.StandardOutput.ReadLineAsync();
                if (string.IsNullOrWhiteSpace(line)) continue;

                JsonObject? obj;
                try { obj = JsonNode.Parse(line)?.AsObject(); }
                catch { continue; }

                if (obj?["id"] is JsonValue v && v.TryGetValue<int>(out var rid) && rid == idToWait)
                    return obj;
                // ignore other messages (initialize, tools/list, logs)
            }
            return null;
        }

        // 1) initialize (id=1) — we won’t print its response
        await RpcAsync(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "initialize",
            @params = new
            {
                protocolVersion = "2025-06-18",
                capabilities = new { tools = new { listChanged = true } },
                clientInfo = new { name = "McpClientDemo", version = "0.1.0" }
            }
        }, idToWait: 1);

        // 2) tools/list (id=2) — also quiet
        await RpcAsync(new { jsonrpc = "2.0", id = 2, method = "tools/list" }, idToWait: 2);

        Console.WriteLine("Type city (or blank to exit). You can enter 'City,CC' (e.g., London,GB).");
        while (true)
        {
            Console.Write("City: ");
            var input = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(input)) break;

            string city = input, country = null!;
            var parts = input.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2) { city = parts[0]; country = parts[1]; }

            var id = 3; // reuse the same id; we wait for it each time

            var resp = await RpcAsync(new
            {
                jsonrpc = "2.0",
                id,
                method = "tools/call",
                @params = new
                {
                    name = "get_current_weather", // exact snake_case from tools/list
                    arguments = new { city, countryCode = country }
                }
            }, idToWait: id);

            // print a clean line for the user
            if (resp is null) { Console.WriteLine("No response from server."); continue; }
            if (resp["error"] is JsonObject err && err["message"] is JsonValue em)
            {
                Console.WriteLine($"Error: {em.ToString()}");
                continue;
            }

            // Try to show nice text if the server wrapped content; otherwise dump result
            var result = resp["result"] as JsonObject;
            if (result?["content"] is JsonArray content &&
                content.FirstOrDefault() is JsonObject first &&
                first["text"] is JsonValue textVal)
            {
                Console.WriteLine(textVal.ToString());
            }
            else
            {
                Console.WriteLine(result?.ToJsonString() ?? resp.ToJsonString());
            }
        }

        try { p.Kill(); } catch { }
    }
}
