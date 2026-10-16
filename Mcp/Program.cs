using Railway.Engine;
using Railway.Mcp.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text;

var builder = Host.CreateApplicationBuilder(args);

// Configure all logs to go to stderr (stdout is used for the MCP protocol messages).
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

// Register Engine services
builder.Services.AddEngine();

// Add the MCP services: the transport to use (stdio) and the tools to register.
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<ListPipelinesTool>()
    .WithTools<ValidatePipelineTool>()
    .WithTools<RunPipelineTool>()
    .WithTools<DashboardTool>()
    .WithTools<RunsTool>();

var host = builder.Build();

try
{
    // Initialize Engine (mirrors Cli/Program.cs)
    await host.Services.InitializeEngineAsync();
}
catch (InvalidOperationException ex)
{
    var stderr = Console.OpenStandardError();
    var bytes = Encoding.UTF8.GetBytes($"{ex.Message}\n");
    stderr.Write(bytes, 0, bytes.Length);
    Environment.Exit(1);
}

await host.RunAsync();
