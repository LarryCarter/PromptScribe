using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PromptScribe;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static async Task<int> Main(string[] args)
    {
        try
        {
            var options = ParseArgs(args);
            if (options.Command is "help" or "--help" or "-h")
            {
                PrintHelp();
                return 0;
            }

            if (options.Command == "doctor")
                return await DoctorAsync(options);

            var repoRoot = ResolveRepositoryRoot(options.Repository);
            var projectConfig = LoadProjectConfig(repoRoot, options.Config);
            var promptSetPath = ResolvePromptSetPath(repoRoot, options.PromptSet ?? projectConfig?.PromptSet);
            var promptSet = LoadJson<PromptSetConfig>(Path.Combine(promptSetPath, "promptset.json"));
            var applications = ResolveApplications(repoRoot, projectConfig, options.Application);
            var stateStore = new StateStore(repoRoot, promptSet.Name);
            var state = stateStore.LoadOrCreate();

            switch (options.Command)
            {
                case "status":
                    PrintStatus(state, applications, promptSet);
                    return 0;
                case "list":
                    PrintList(repoRoot, promptSetPath, applications, promptSet);
                    return 0;
                case "retry-failed":
                    options.RetryFailedOnly = true;
                    break;
                case "resume":
                case "run":
                    break;
                default:
                    Console.Error.WriteLine($"Unknown command '{options.Command}'.");
                    PrintHelp();
                    return 2;
            }

            if (!options.DryRun && !await CommandExistsAsync("copilot", "--version"))
            {
                Console.Error.WriteLine("PromptScribe cannot find GitHub Copilot CLI on PATH. Run 'PromptScribe doctor' for details.");
                return 3;
            }

            var gitBefore = await GitStatusAsync(repoRoot);
            var runner = new Runner(repoRoot, promptSetPath, promptSet, projectConfig, stateStore, state, options);
            var result = await runner.RunAsync(applications);
            var gitAfter = await GitStatusAsync(repoRoot);

            if (gitBefore != gitAfter)
            {
                Console.WriteLine();
                Console.WriteLine("Repository changes were detected during the run. Review 'git status' before committing anything.");
            }

            return result;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"PromptScribe failed: {ex.Message}");
            return 1;
        }
    }

    private static CliOptions ParseArgs(string[] args)
    {
        var o = new CliOptions();
        var index = 0;
        if (args.Length > 0 && !args[0].StartsWith('-'))
        {
            o.Command = args[0].ToLowerInvariant();
            index = 1;
        }

        while (index < args.Length)
        {
            var arg = args[index++];
            string Next() => index < args.Length ? args[index++] : throw new ArgumentException($"Missing value after {arg}");

            switch (arg)
            {
                case "--repository": case "-r": o.Repository = Next(); break;
                case "--promptset": o.PromptSet = Next(); break;
                case "--config": o.Config = Next(); break;
                case "--application": case "-a": o.Application = Next(); break;
                case "--batch": case "-b": o.Batch = Next(); break;
                case "--prompt": case "-p": o.Prompt = Next(); break;
                case "--model": case "-m": o.Model = Next(); break;
                case "--force": o.Force = true; break;
                case "--dry-run": o.DryRun = true; break;
                case "--set":
                {
                    var value = Next();
                    var split = value.IndexOf('=');
                    if (split <= 0) throw new ArgumentException("--set requires NAME=VALUE");
                    o.Overrides[value[..split]] = value[(split + 1)..];
                    break;
                }
                case "--help": case "-h": o.Command = "help"; break;
                default: throw new ArgumentException($"Unknown argument '{arg}'");
            }
        }

        return o;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("PromptScribe - reusable Copilot-driven prompt batch runner\n");
        Console.WriteLine("Usage:");
        Console.WriteLine("  PromptScribe run [options]");
        Console.WriteLine("  PromptScribe resume [options]");
        Console.WriteLine("  PromptScribe retry-failed [options]");
        Console.WriteLine("  PromptScribe status [options]");
        Console.WriteLine("  PromptScribe list [options]");
        Console.WriteLine("  PromptScribe doctor [options]\n");
        Console.WriteLine("Options:");
        Console.WriteLine("  -r, --repository <path>     Repository root override");
        Console.WriteLine("      --promptset <path|name> Prompt set override");
        Console.WriteLine("      --config <path>         Project config override");
        Console.WriteLine("  -a, --application <name>    Run one configured application");
        Console.WriteLine("  -b, --batch <name>          Run one batch");
        Console.WriteLine("  -p, --prompt <file|stem>    Run one prompt");
        Console.WriteLine("  -m, --model <model>         Override Copilot model");
        Console.WriteLine("      --set NAME=VALUE        Override/add a prompt variable (repeatable)");
        Console.WriteLine("      --force                 Rerun prompts already marked complete");
        Console.WriteLine("      --dry-run               Show execution without invoking Copilot\n");
        Console.WriteLine("Prompt variables use {{NAME}} tokens. Built-ins include:");
        Console.WriteLine("  APPLICATION_NAME, PROJECT_NAME, REPOSITORY_ROOT, SOLUTION_NAME, PROMPT_SET, BATCH_NAME");
    }

    private static string ResolveRepositoryRoot(string? explicitPath)
    {
        var start = Path.GetFullPath(explicitPath ?? Directory.GetCurrentDirectory());
        var git = TryRunCapture("git", ["-C", start, "rev-parse", "--show-toplevel"]);
        if (git.ExitCode == 0 && !string.IsNullOrWhiteSpace(git.StdOut))
            return Path.GetFullPath(git.StdOut.Trim());
        return start;
    }

    private static ProjectConfig? LoadProjectConfig(string repoRoot, string? explicitPath)
    {
        var path = explicitPath is not null
            ? Path.GetFullPath(explicitPath)
            : Path.Combine(repoRoot, ".promptscribe.json");
        return File.Exists(path) ? LoadJson<ProjectConfig>(path) : null;
    }

    private static string ResolvePromptSetPath(string repoRoot, string? requested)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(requested))
        {
            if (Path.IsPathRooted(requested)) candidates.Add(requested);
            candidates.Add(Path.Combine(repoRoot, requested));
            candidates.Add(Path.Combine(repoRoot, ".promptscribe", "promptsets", requested));
            candidates.Add(Path.Combine(AppContext.BaseDirectory, "promptsets", requested));
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PromptScribe", "promptsets", requested));
        }
        else
        {
            candidates.Add(Path.Combine(repoRoot, ".promptscribe", "promptsets", "default"));
            candidates.Add(Path.Combine(AppContext.BaseDirectory, "promptsets", "legacy-analysis"));
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PromptScribe", "promptsets", "legacy-analysis"));
        }

        foreach (var candidate in candidates.Select(Path.GetFullPath))
            if (File.Exists(Path.Combine(candidate, "promptset.json"))) return candidate;

        throw new FileNotFoundException("No promptset.json found. Use --promptset <path> or install a prompt set beside PromptScribe / under %LOCALAPPDATA%\\PromptScribe\\promptsets.");
    }

    private static List<ApplicationConfig> ResolveApplications(string repoRoot, ProjectConfig? config, string? filter)
    {
        List<ApplicationConfig> apps;
        if (config?.Applications.Count > 0)
        {
            apps = config.Applications;
        }
        else
        {
            var sln = Directory.EnumerateFiles(repoRoot, "*.sln", SearchOption.TopDirectoryOnly).FirstOrDefault();
            var name = config?.ProjectName ?? (sln is not null ? Path.GetFileNameWithoutExtension(sln) : new DirectoryInfo(repoRoot).Name);
            apps = [new ApplicationConfig { Name = name }];
        }

        if (!string.IsNullOrWhiteSpace(filter))
        {
            apps = apps.Where(a => string.Equals(a.Name, filter, StringComparison.OrdinalIgnoreCase)).ToList();
            if (apps.Count == 0) throw new InvalidOperationException($"Application '{filter}' is not configured.");
        }
        return apps;
    }

    private static T LoadJson<T>(string path) where T : new()
    {
        var text = File.ReadAllText(path);
        return JsonSerializer.Deserialize<T>(text, JsonOptions) ?? new T();
    }

    private static async Task<int> DoctorAsync(CliOptions options)
    {
        Console.WriteLine("PromptScribe doctor");
        Console.WriteLine($"Current directory: {Directory.GetCurrentDirectory()}");
        Console.WriteLine($"Repository: {ResolveRepositoryRoot(options.Repository)}");
        Console.WriteLine($"Git: {(await CommandExistsAsync("git", "--version") ? "OK" : "NOT FOUND")}");
        Console.WriteLine($"Copilot CLI: {(await CommandExistsAsync("copilot", "--version") ? "OK" : "NOT FOUND")}");
        Console.WriteLine($".NET runtime: {Environment.Version}");
        return 0;
    }

    private static async Task<bool> CommandExistsAsync(string command, string arg)
    {
        try
        {
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo(command, arg)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            p.Start();
            await p.WaitForExitAsync();
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    private static async Task<string> GitStatusAsync(string repoRoot)
    {
        try
        {
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo("git")
                {
                    WorkingDirectory = repoRoot,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            p.StartInfo.ArgumentList.Add("status");
            p.StartInfo.ArgumentList.Add("--porcelain");
            p.Start();
            var output = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            return output;
        }
        catch { return string.Empty; }
    }

    private static void PrintStatus(RunState state, List<ApplicationConfig> apps, PromptSetConfig set)
    {
        Console.WriteLine($"Prompt Set: {set.Name}");
        foreach (var app in apps)
        {
            Console.WriteLine($"\n{app.Name}");
            foreach (var batch in set.Batches)
            {
                var bs = state.Applications.GetValueOrDefault(app.Name)?.Batches.GetValueOrDefault(batch.Name);
                var done = bs?.Prompts.Count(p => p.Value.Status == "complete") ?? 0;
                var failed = bs?.Prompts.Count(p => p.Value.Status == "failed") ?? 0;
                Console.WriteLine($"  {batch.Name}: {done} complete, {failed} failed");
            }
        }
    }

    private static void PrintList(string repo, string promptSetPath, List<ApplicationConfig> apps, PromptSetConfig set)
    {
        Console.WriteLine($"Repository: {repo}");
        Console.WriteLine($"Prompt set: {set.Name} ({promptSetPath})");
        Console.WriteLine("Applications:");
        foreach (var app in apps) Console.WriteLine($"  - {app.Name}");
        Console.WriteLine("Batches:");
        foreach (var b in set.Batches) Console.WriteLine($"  - {b.Name} -> {b.Path} (model: {b.Model ?? set.DefaultModel ?? "Copilot default"})");
    }

    private static (int ExitCode, string StdOut, string StdErr) TryRunCapture(string file, IEnumerable<string> args)
    {
        try
        {
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo(file)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            foreach (var a in args) p.StartInfo.ArgumentList.Add(a);
            p.Start();
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit();
            return (p.ExitCode, stdout, stderr);
        }
        catch (Exception ex) { return (-1, string.Empty, ex.Message); }
    }
}

internal sealed class StateStore
{
    private readonly string _path;
    private readonly string _repo;
    private readonly string _set;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public StateStore(string repositoryRoot, string promptSetName)
    {
        _repo = repositoryRoot;
        _set = promptSetName;
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(repositoryRoot + "|" + promptSetName)))[..16];
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PromptScribe", "state");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, $"{Sanitize(promptSetName)}-{key}.json");
    }

    public RunState LoadOrCreate()
    {
        if (!File.Exists(_path)) return new RunState { RepositoryRoot = _repo, PromptSetName = _set };
        return JsonSerializer.Deserialize<RunState>(File.ReadAllText(_path), JsonOptions) ?? new RunState { RepositoryRoot = _repo, PromptSetName = _set };
    }

    public void Save(RunState state) => File.WriteAllText(_path, JsonSerializer.Serialize(state, JsonOptions));

    public static string Sanitize(string value) => Regex.Replace(value, "[^A-Za-z0-9._-]+", "-");
}

internal sealed class Runner
{
    private readonly string _repo;
    private readonly string _setPath;
    private readonly PromptSetConfig _set;
    private readonly ProjectConfig? _project;
    private readonly StateStore _stateStore;
    private readonly RunState _state;
    private readonly CliOptions _options;

    public Runner(string repo, string setPath, PromptSetConfig set, ProjectConfig? project, StateStore stateStore, RunState state, CliOptions options)
        => (_repo, _setPath, _set, _project, _stateStore, _state, _options) = (repo, setPath, set, project, stateStore, state, options);

    public async Task<int> RunAsync(List<ApplicationConfig> apps)
    {
        var anyFailed = false;
        foreach (var app in apps)
        {
            Console.WriteLine($"\n=== {app.Name} ===");
            foreach (var batch in _set.Batches.Where(BatchSelected))
            {
                var ok = await RunBatchAsync(app, batch);
                if (!ok)
                {
                    anyFailed = true;
                    Console.WriteLine($"Batch '{batch.Name}' stopped for {app.Name}.");
                    break;
                }
            }
        }
        return anyFailed ? 4 : 0;
    }

    private bool BatchSelected(BatchConfig b) => string.IsNullOrWhiteSpace(_options.Batch) || string.Equals(b.Name, _options.Batch, StringComparison.OrdinalIgnoreCase);

    private async Task<bool> RunBatchAsync(ApplicationConfig app, BatchConfig batch)
    {
        var appState = GetOrCreate(_state.Applications, app.Name, () => new ApplicationRunState());
        var batchState = GetOrCreate(appState.Batches, batch.Name, () => new BatchRunState());
        if (batch.PreserveContext && string.IsNullOrWhiteSpace(batchState.SessionId))
            batchState.SessionId = Guid.NewGuid().ToString();

        var folder = Path.Combine(_setPath, batch.Path);
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException($"Batch folder not found: {folder}");
        var prompts = Directory.EnumerateFiles(folder, "*.md", SearchOption.TopDirectoryOnly).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToList();
        if (prompts.Count == 0) throw new InvalidOperationException($"No .md prompts found in {folder}");

        foreach (var promptFile in prompts.Where(PromptSelected))
        {
            var key = Path.GetFileName(promptFile);
            var ps = GetOrCreate(batchState.Prompts, key, () => new PromptRunState());
            if (!_options.Force)
            {
                if (_options.RetryFailedOnly && ps.Status != "failed") continue;
                if (!_options.RetryFailedOnly && ps.Status == "complete") continue;
            }

            var variables = BuildVariables(app, batch);
            var prompt = Render(File.ReadAllText(promptFile), variables);
            var model = _options.Model ?? batch.Model ?? _set.DefaultModel;

            Console.WriteLine($"[{app.Name}] [{batch.Name}] {key} {(model is null ? string.Empty : $"({model})")}");
            if (_options.DryRun)
            {
                Console.WriteLine("  DRY RUN - not invoking Copilot");
                continue;
            }

            ps.Attempts++;
            ps.LastRunUtc = DateTimeOffset.UtcNow;
            ps.Model = model;
            ps.Status = "running";
            _stateStore.Save(_state);

            var result = await RunCopilotAsync(prompt, model, batch.PreserveContext ? batchState.SessionId : Guid.NewGuid().ToString(), app.Name, batch.Name, key);
            ps.ExitCode = result.ExitCode;
            ps.LogFile = result.LogFile;
            ps.Status = result.ExitCode == 0 ? "complete" : "failed";
            _stateStore.Save(_state);

            if (result.ExitCode != 0)
            {
                Console.Error.WriteLine($"  FAILED ({result.ExitCode}). Log: {result.LogFile}");
                return false;
            }
            Console.WriteLine("  complete");
        }
        return true;
    }

    private bool PromptSelected(string path)
    {
        if (string.IsNullOrWhiteSpace(_options.Prompt)) return true;
        var file = Path.GetFileName(path);
        var stem = Path.GetFileNameWithoutExtension(path);
        return string.Equals(file, _options.Prompt, StringComparison.OrdinalIgnoreCase) || string.Equals(stem, _options.Prompt, StringComparison.OrdinalIgnoreCase);
    }

    private Dictionary<string, string> BuildVariables(ApplicationConfig app, BatchConfig batch)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Merge(d, _set.Variables);
        if (_project is not null) Merge(d, _project.Variables);
        Merge(d, batch.Variables);
        Merge(d, app.Variables);

        var solution = Directory.EnumerateFiles(_repo, "*.sln", SearchOption.TopDirectoryOnly).FirstOrDefault();
        d["APPLICATION_NAME"] = app.Name;
        d["PROJECT_NAME"] = _project?.ProjectName ?? (solution is not null ? Path.GetFileNameWithoutExtension(solution) : new DirectoryInfo(_repo).Name);
        d["REPOSITORY_ROOT"] = _repo;
        d["SOLUTION_NAME"] = solution is null ? string.Empty : Path.GetFileName(solution);
        d["PROMPT_SET"] = _set.Name;
        d["BATCH_NAME"] = batch.Name;
        Merge(d, _options.Overrides);
        return d;
    }

    private static string Render(string text, Dictionary<string, string> variables)
    {
        return Regex.Replace(text, "\\{\\{([A-Za-z0-9_.-]+)\\}\\}", m =>
        {
            var key = m.Groups[1].Value;
            return variables.TryGetValue(key, out var value) ? value : m.Value;
        });
    }

    private async Task<(int ExitCode, string LogFile)> RunCopilotAsync(string prompt, string? model, string? sessionId, string app, string batch, string promptName)
    {
        var logs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PromptScribe", "logs", StateStore.Sanitize(new DirectoryInfo(_repo).Name), StateStore.Sanitize(app));
        Directory.CreateDirectory(logs);
        var logFile = Path.Combine(logs, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{StateStore.Sanitize(batch)}-{StateStore.Sanitize(promptName)}.jsonl");

        var psi = new ProcessStartInfo("copilot")
        {
            WorkingDirectory = _repo,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add(prompt);
        psi.ArgumentList.Add("--no-ask-user");
        psi.ArgumentList.Add("--output-format=json");
        psi.ArgumentList.Add("--no-remote");
        psi.ArgumentList.Add("--no-remote-export");
        psi.ArgumentList.Add("--allow-tool=write");
        psi.ArgumentList.Add("--excluded-tools=web_search,web_fetch");
        psi.ArgumentList.Add("--deny-tool=shell(git push)");
        psi.ArgumentList.Add("--deny-tool=shell(git commit)");
        psi.ArgumentList.Add("--deny-tool=shell(git checkout)");
        psi.ArgumentList.Add("--deny-tool=shell(git reset)");
        psi.ArgumentList.Add("--deny-tool=shell(git clean)");
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            psi.ArgumentList.Add("--session-id");
            psi.ArgumentList.Add(sessionId);
        }
        if (!string.IsNullOrWhiteSpace(model))
        {
            psi.ArgumentList.Add("--model");
            psi.ArgumentList.Add(model);
        }

        using var process = new Process { StartInfo = psi };
        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        File.WriteAllText(logFile, stdout + (string.IsNullOrWhiteSpace(stderr) ? string.Empty : Environment.NewLine + "{\"promptscribe_stderr\":" + JsonSerializer.Serialize(stderr) + "}"));
        return (process.ExitCode, logFile);
    }

    private static void Merge(Dictionary<string, string> target, Dictionary<string, string> source)
    {
        foreach (var (k, v) in source) target[k] = v;
    }

    private static TValue GetOrCreate<TKey, TValue>(Dictionary<TKey, TValue> dict, TKey key, Func<TValue> factory) where TKey : notnull
    {
        if (dict.TryGetValue(key, out var value)) return value;
        value = factory();
        dict[key] = value;
        return value;
    }
}
