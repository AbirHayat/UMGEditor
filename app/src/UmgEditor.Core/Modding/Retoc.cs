using System.Diagnostics;
using System.Security.Cryptography;

namespace UmgEditor.Core.Modding;

public sealed record RetocResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Ok => ExitCode == 0;
    public string Output => (StdOut + (StdErr.Length > 0 ? Environment.NewLine + StdErr : "")).Trim();
}

/// <summary>Runs retoc commands. An interface so tests can wrap the real tool (e.g. make `verify` fail).</summary>
public interface IRetoc
{
    string ExePath { get; }
    RetocResult Run(params string[] args);
}

/// <summary>The retoc executable from GameConfig.RetocPath (this retoc has no --version; identify it by sha256).</summary>
public sealed class RetocProcess(string exePath) : IRetoc
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    public string ExePath { get; } = Path.GetFullPath(exePath);

    public RetocResult Run(params string[] args)
    {
        if (!File.Exists(ExePath)) return new RetocResult(-1, "", $"retoc not found at {ExePath} (setting RetocPath in umgeditor.json)");
        var psi = new ProcessStartInfo(ExePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(ExePath)!,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(Timeout))
        {
            p.Kill(entireProcessTree: true);
            return new RetocResult(-1, stdout.Result, $"retoc {args.FirstOrDefault()} timed out after {Timeout.TotalMinutes} min");
        }
        return new RetocResult(p.ExitCode, stdout.Result, stderr.Result);
    }

    public static string Sha256Of(string path) => File.Exists(path)
        ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant()
        : "";
}
