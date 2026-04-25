using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace TinyBoss.Installer;

public record CliDefinition(
    string Id,
    string DisplayName,
    string DetectCommand,
    string DetectArgs,
    CliInstallKind InstallKind,
    string InstallTarget,
    bool IsAvailable,
    string Description = "");

public enum CliInstallKind { Npm, Pip, Stub }

/// <summary>
/// Detects and installs Node.js + npm-based CLI tools.
/// Uses absolute paths after Node install to avoid PATH refresh issues.
/// </summary>
public static class CliInstaller
{
    private const int MinNodeMajor = 18;

    // Canonical npm global bin — where "npm install -g" places .cmd shims
    private static string NpmGlobalBin =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm");

    // Known Node.js install locations (per-user + machine)
    private static readonly string[] NodeSearchPaths =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "nodejs"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "nodejs"),
    ];

    // Known Python install locations
    private static readonly string[] PythonSearchPaths =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Python"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Python"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Python311"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Python312"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Python313"),
    ];

    // Canonical pip/pipx user bin — where pip install --user / pipx places scripts
    private static string PipUserBin =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "Python", "Scripts");

    private static string PipxBin =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "pipx", "venvs");

    // ── CLI Definitions ──────────────────────────────────────────────────

    public static IReadOnlyList<CliDefinition> AllClis { get; } =
    [
        new("claude", "Claude Code", "claude", "--version", CliInstallKind.Npm,
            "@anthropic-ai/claude-code", IsAvailable: true,
            "Anthropic's Claude CLI for terminal"),

        new("codex", "OpenAI Codex", "codex", "--version", CliInstallKind.Npm,
            "@openai/codex", IsAvailable: true,
            "OpenAI's Codex CLI for terminal"),

        new("ghcp", "GitHub Copilot CLI", "ghcp", "--version", CliInstallKind.Stub,
            "", IsAvailable: false,
            "Coming soon — install method TBD"),

        new("inferno", "Inferno", "inferno", "--version", CliInstallKind.Pip,
            "inferno-local", IsAvailable: true,
            "Local LLM runtime — connects to any OpenAI-compatible endpoint"),

        new("qwen", "Qwen CLI", "qwen", "--version", CliInstallKind.Stub,
            "", IsAvailable: false,
            "Coming soon — npm package TBD"),
    ];

    // ── Node.js Detection ────────────────────────────────────────────────

    public static async Task<CheckResult> DetectNodeJs(CancellationToken ct)
    {
        var nodePath = ResolveNode();
        if (nodePath is null)
            return new(false, "", "Node.js not found");

        var npmPath = ResolveNpm();
        if (npmPath is null)
            return new(false, "", "Node.js found but npm is missing");

        var nodeVer = await RunCapture(nodePath, "--version", ct);
        if (string.IsNullOrWhiteSpace(nodeVer))
            return new(false, "", "Could not read Node.js version");

        // Parse version (e.g., "v20.11.1" → 20)
        var clean = nodeVer.Trim().TrimStart('v');
        if (int.TryParse(clean.Split('.')[0], out var major) && major < MinNodeMajor)
            return new(false, clean, $"Node.js {clean} is too old (need ≥ {MinNodeMajor})");

        var npmVer = await RunCapture(npmPath, "--version", ct);
        return new(true, $"node {clean}, npm {npmVer?.Trim() ?? "?"}");
    }

    // ── Node.js Installation ─────────────────────────────────────────────

    public static async Task<CheckResult> InstallNodeJs(
        Action<int, string> progress, CancellationToken ct)
    {
        progress(5, "Checking for winget…");

        // Prefer winget (no admin needed for per-user)
        var winget = ResolveCommand("winget");
        if (winget is not null)
        {
            progress(15, "Installing Node.js LTS via winget…");
            var (exitCode, output) = await RunWithOutput(winget,
                "install --id OpenJS.NodeJS.LTS --accept-package-agreements --accept-source-agreements",
                ct);

            if (exitCode == 0)
            {
                progress(85, "Verifying Node.js…");
                // Refresh our view of installed paths
                await Task.Delay(2000, ct);
                var detect = await DetectNodeJs(ct);
                progress(100, detect.Success ? $"Node.js installed ✅ ({detect.Version})" : "Installed but not detected");
                return detect;
            }

            // Winget failed — fall through to direct download
            progress(20, "winget install failed, trying direct download…");
        }

        // Fallback: download MSI from nodejs.org
        progress(25, "Downloading Node.js LTS installer…");
        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            _ => "x64",
        };
        var msiUrl = $"https://nodejs.org/dist/v22.15.0/node-v22.15.0-{arch}.msi";
        var tempMsi = Path.Combine(Path.GetTempPath(), $"node-lts-{arch}.msi");

        try
        {
            using var http = new HttpClient();
            http.Timeout = TimeSpan.FromMinutes(5);
            var response = await http.GetAsync(msiUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? 0;
            await using var fs = File.Create(tempMsi);
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[81920];
            long downloaded = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer, ct)) > 0)
            {
                await fs.WriteAsync(buffer.AsMemory(0, read), ct);
                downloaded += read;
                if (totalBytes > 0)
                {
                    var pct = (int)(25 + 50.0 * downloaded / totalBytes);
                    progress(Math.Min(pct, 75), $"Downloading… {downloaded / (1024 * 1024)} MB");
                }
            }
        }
        catch (Exception ex)
        {
            return new(false, "", $"Download failed: {ex.Message}");
        }

        // Run MSI per-user silent install
        progress(78, "Running Node.js installer (silent)…");
        var (msiExit, msiOut) = await RunWithOutput("msiexec",
            $"/i \"{tempMsi}\" /qn /norestart", ct);

        try { File.Delete(tempMsi); } catch { /* best effort */ }

        if (msiExit != 0)
            return new(false, "", $"MSI install exited with code {msiExit}");

        progress(90, "Verifying Node.js…");
        await Task.Delay(2000, ct);
        var result = await DetectNodeJs(ct);
        progress(100, result.Success ? $"Node.js installed ✅ ({result.Version})" : "Installed but not detected");
        return result;
    }

    // ── Python Detection ─────────────────────────────────────────────────

    public static async Task<CheckResult> DetectPython(CancellationToken ct)
    {
        var pythonPath = ResolvePython();
        if (pythonPath is null)
            return new(false, "", "Python not found");

        var ver = await RunCapture(pythonPath, "--version", ct);
        if (string.IsNullOrWhiteSpace(ver))
            return new(false, "", "Python found but version check failed");

        var clean = ver.Trim().Replace("Python ", "");
        if (int.TryParse(clean.Split('.')[1], out var minor) && minor < 11)
            return new(false, clean, $"Python {clean} is too old (need ≥ 3.11)");

        var pipPath = ResolvePip();
        return new(true, $"Python {clean}" + (pipPath is not null ? ", pip ✓" : ", pip missing"));
    }

    // ── Python Installation ──────────────────────────────────────────────

    public static async Task<CheckResult> InstallPython(
        Action<int, string> progress, CancellationToken ct)
    {
        progress(5, "Checking for winget…");

        var winget = ResolveCommand("winget");
        if (winget is not null)
        {
            progress(15, "Installing Python 3.12 via winget…");
            var (exitCode, output) = await RunWithOutput(winget,
                "install --id Python.Python.3.12 --accept-package-agreements --accept-source-agreements",
                ct);

            if (exitCode == 0)
            {
                progress(85, "Verifying Python…");
                await Task.Delay(2000, ct);
                var detect = await DetectPython(ct);
                progress(100, detect.Success ? $"Python installed ✅ ({detect.Version})" : "Installed but not detected");
                return detect;
            }

            progress(20, "winget install failed, trying python.org…");
        }

        // Fallback: point user to python.org
        progress(100, "Please install Python 3.11+ from python.org");
        return new(false, "", "Auto-install failed — install Python 3.11+ from python.org");
    }

    // ── CLI Detection ────────────────────────────────────────────────────

    public static async Task<CheckResult> DetectCli(CliDefinition cli, CancellationToken ct)
    {
        if (!cli.IsAvailable)
            return new(false, "", cli.Description);

        // Check appropriate bin paths based on install kind
        var cmdPath = cli.InstallKind == CliInstallKind.Pip
            ? ResolvePipCli(cli.DetectCommand)
            : ResolveCli(cli.DetectCommand);
        if (cmdPath is null)
            return new(false, "", "Not installed");

        var version = await RunCapture(cmdPath, cli.DetectArgs, ct);
        return string.IsNullOrWhiteSpace(version)
            ? new(false, "", "Found but version check failed")
            : new(true, version.Trim());
    }

    // ── CLI Installation ─────────────────────────────────────────────────

    public static async Task<CheckResult> InstallCli(
        CliDefinition cli, Action<int, string> progress, CancellationToken ct)
    {
        if (!cli.IsAvailable || cli.InstallKind == CliInstallKind.Stub)
            return new(false, "", $"{cli.DisplayName} is not yet available for install");

        if (cli.InstallKind == CliInstallKind.Pip)
            return await InstallPipCli(cli, progress, ct);

        progress(10, $"Locating npm…");
        var npm = ResolveNpm();
        if (npm is null)
            return new(false, "", "npm not found — Node.js must be installed first");

        progress(30, $"Installing {cli.InstallTarget}…");
        var (exit, output) = await RunWithOutput(npm,
            $"install -g {cli.InstallTarget}", ct);

        if (exit != 0)
            return new(false, "", $"npm install failed (exit {exit}): {output}");

        progress(80, "Verifying…");
        var detect = await DetectCli(cli, ct);
        progress(100, detect.Success
            ? $"{cli.DisplayName} installed ✅ ({detect.Version})"
            : $"Installed but detection failed");
        return detect;
    }

    // ── Pip CLI Installation ─────────────────────────────────────────────

    private static async Task<CheckResult> InstallPipCli(
        CliDefinition cli, Action<int, string> progress, CancellationToken ct)
    {
        progress(10, "Locating pip…");
        var pip = ResolvePip();
        if (pip is null)
        {
            // Try pipx as fallback
            var pipx = ResolveCommand("pipx");
            if (pipx is not null)
            {
                progress(30, $"Installing {cli.InstallTarget} via pipx…");
                var (pipxExit, pipxOut) = await RunWithOutput(pipx,
                    $"install {cli.InstallTarget}", ct);
                if (pipxExit == 0)
                {
                    progress(80, "Verifying…");
                    var pipxDetect = await DetectCli(cli, ct);
                    progress(100, pipxDetect.Success
                        ? $"{cli.DisplayName} installed ✅ ({pipxDetect.Version})"
                        : "Installed but detection failed");
                    return pipxDetect;
                }
                return new(false, "", $"pipx install failed (exit {pipxExit}): {pipxOut}");
            }
            return new(false, "", "pip/pipx not found — Python 3.11+ must be installed first");
        }

        progress(30, $"Installing {cli.InstallTarget} via pip…");
        var (exit, output) = await RunWithOutput(pip,
            $"install {cli.InstallTarget}", ct);

        if (exit != 0)
            return new(false, "", $"pip install failed (exit {exit}): {output}");

        progress(80, "Verifying…");
        var detect = await DetectCli(cli, ct);
        progress(100, detect.Success
            ? $"{cli.DisplayName} installed ✅ ({detect.Version})"
            : "Installed but detection failed");
        return detect;
    }

    // ── Path Resolution ──────────────────────────────────────────────────

    private static string? ResolveNode()
    {
        // Check known install directories first
        foreach (var dir in NodeSearchPaths)
        {
            var exe = Path.Combine(dir, "node.exe");
            if (File.Exists(exe)) return exe;
        }
        // Fall back to PATH
        return ResolveCommand("node");
    }

    private static string? ResolveNpm()
    {
        // npm.cmd lives alongside node.exe in the Node install dir
        foreach (var dir in NodeSearchPaths)
        {
            var cmd = Path.Combine(dir, "npm.cmd");
            if (File.Exists(cmd)) return cmd;
        }
        // Also check npm global bin
        var globalNpm = Path.Combine(NpmGlobalBin, "npm.cmd");
        if (File.Exists(globalNpm)) return globalNpm;
        // Fall back to PATH
        return ResolveCommand("npm");
    }

    private static string? ResolveCli(string command)
    {
        // Check npm global bin (most reliable after fresh install)
        var cmdFile = Path.Combine(NpmGlobalBin, command + ".cmd");
        if (File.Exists(cmdFile)) return cmdFile;

        var exeFile = Path.Combine(NpmGlobalBin, command + ".exe");
        if (File.Exists(exeFile)) return exeFile;

        // Fall back to PATH
        return ResolveCommand(command);
    }

    private static string? ResolvePython()
    {
        // Check known install directories (recurse into version subdirs)
        foreach (var basePath in PythonSearchPaths)
        {
            if (Directory.Exists(basePath))
            {
                // Direct check (e.g., C:\Program Files\Python312\python.exe)
                var direct = Path.Combine(basePath, "python.exe");
                if (File.Exists(direct)) return direct;

                // Check version subdirectories (e.g., Python312, Python311)
                try
                {
                    foreach (var sub in Directory.GetDirectories(basePath, "Python3*"))
                    {
                        var exe = Path.Combine(sub, "python.exe");
                        if (File.Exists(exe)) return exe;
                    }
                }
                catch { /* access denied or similar */ }
            }
        }
        // Fall back to PATH
        return ResolveCommand("python") ?? ResolveCommand("python3");
    }

    private static string? ResolvePip()
    {
        // pip typically lives alongside python in Scripts/
        var python = ResolvePython();
        if (python is not null)
        {
            var scriptsDir = Path.Combine(Path.GetDirectoryName(python)!, "Scripts");
            var pip = Path.Combine(scriptsDir, "pip.exe");
            if (File.Exists(pip)) return pip;
            var pip3 = Path.Combine(scriptsDir, "pip3.exe");
            if (File.Exists(pip3)) return pip3;
        }
        return ResolveCommand("pip") ?? ResolveCommand("pip3");
    }

    private static string? ResolvePipCli(string command)
    {
        // Check Python Scripts dirs
        var python = ResolvePython();
        if (python is not null)
        {
            var scriptsDir = Path.Combine(Path.GetDirectoryName(python)!, "Scripts");
            var exe = Path.Combine(scriptsDir, command + ".exe");
            if (File.Exists(exe)) return exe;
        }

        // Check pipx venvs
        if (Directory.Exists(PipxBin))
        {
            var venvBin = Path.Combine(PipxBin, command, "Scripts", command + ".exe");
            if (File.Exists(venvBin)) return venvBin;
        }

        // Fall back to PATH
        return ResolveCommand(command);
    }

    private static string? ResolveCommand(string command)
    {
        try
        {
            var psi = new ProcessStartInfo("where", command)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return null;
            var path = proc.StandardOutput.ReadLine()?.Trim();
            proc.WaitForExit(5000);
            return !string.IsNullOrEmpty(path) && File.Exists(path) ? path : null;
        }
        catch { return null; }
    }

    // ── Process Helpers ──────────────────────────────────────────────────

    private static async Task<string?> RunCapture(string exe, string args, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return null;
            var output = await proc.StandardOutput.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            return proc.ExitCode == 0 ? output : null;
        }
        catch { return null; }
    }

    private static async Task<(int ExitCode, string Output)> RunWithOutput(
        string exe, string args, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return (-1, "Failed to start process");
            var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
            var stderr = await proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            return (proc.ExitCode, string.IsNullOrEmpty(stderr) ? stdout : stderr);
        }
        catch (Exception ex) { return (-1, ex.Message); }
    }
}
