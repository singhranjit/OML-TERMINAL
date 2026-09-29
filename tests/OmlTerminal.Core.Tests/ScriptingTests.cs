using OmlTerminal.Core.Scripting;

namespace OmlTerminal.Core.Tests;

public class ScriptCatalogTests
{
    [Fact]
    public void ScanFindsKnownScriptTypesAndParsesLeadingDescription()
    {
        var dir = Path.Combine(Path.GetTempPath(), "oml-script-scan-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "hello.ps1"), "# Description: Says hello\nWrite-Output 'hi'\n");
            File.WriteAllText(Path.Combine(dir, "plain.py"), "print('hi')\n");
            File.WriteAllText(Path.Combine(dir, "notes.txt"), "not a script - ignored");

            var scripts = ScriptCatalog.Scan(dir);

            Assert.Equal(2, scripts.Count);
            var hello = scripts.Single(s => s.Name == "hello");
            Assert.Equal("Says hello", hello.Description);
            Assert.Equal("powershell.exe", hello.Interpreter);
            var plain = scripts.Single(s => s.Name == "plain");
            Assert.Equal("", plain.Description);
            Assert.Equal("python", plain.Interpreter);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ScanOfMissingDirectoryReturnsEmpty()
    {
        var scripts = ScriptCatalog.Scan(Path.Combine(Path.GetTempPath(), "oml-does-not-exist-" + Guid.NewGuid()));
        Assert.Empty(scripts);
    }
}

public class ScriptRunnerTests
{
    [Fact]
    public async Task RunPassesSessionContextAsEnvironmentVariablesAndCapturesOutput()
    {
        var dir = Path.Combine(Path.GetTempPath(), "oml-script-run-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            var scriptPath = Path.Combine(dir, "echo-context.cmd");
            File.WriteAllText(scriptPath, "@echo off\r\necho HOST=%OML_SESSION_HOST%\r\necho NAME=%OML_SESSION_NAME%\r\n");
            var script = new ScriptDefinition(scriptPath, "echo-context", "", "cmd.exe", ["/c"]);
            var runner = new ScriptRunner();
            var lines = new List<string>();
            runner.LineReceived += lines.Add;

            var exit = await runner.RunAsync(script, new ScriptContext("test-dev", "10.0.0.5", "admin", "SSH", "Lab", null));

            Assert.Equal(0, exit);
            Assert.Contains(lines, l => l.Contains("HOST=10.0.0.5"));
            Assert.Contains(lines, l => l.Contains("NAME=test-dev"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task RunFeedsStdinToTheScript()
    {
        var dir = Path.Combine(Path.GetTempPath(), "oml-script-stdin-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            var scriptPath = Path.Combine(dir, "reverse.ps1");
            File.WriteAllText(scriptPath, "$input | ForEach-Object { $_ }\n");
            var script = new ScriptDefinition(scriptPath, "reverse", "", "powershell.exe", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File"]);
            var runner = new ScriptRunner();
            var lines = new List<string>();
            runner.LineReceived += lines.Add;

            await runner.RunAsync(script, new ScriptContext(null, null, null, null, null, "hello from stdin"));

            Assert.Contains(lines, l => l.Contains("hello from stdin"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task CancellationKillsTheProcess()
    {
        var dir = Path.Combine(Path.GetTempPath(), "oml-script-cancel-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            var scriptPath = Path.Combine(dir, "sleep.ps1");
            File.WriteAllText(scriptPath, "Start-Sleep -Seconds 30\n");
            var script = new ScriptDefinition(scriptPath, "sleep", "", "powershell.exe", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File"]);
            var runner = new ScriptRunner();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                runner.RunAsync(script, new ScriptContext(null, null, null, null, null, null), cts.Token));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
