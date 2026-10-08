using System.Text.Json.Serialization;

namespace PromptScribe;

public sealed class PromptSetConfig
{
    public string Name { get; set; } = "Prompt Set";
    public string? DefaultModel { get; set; }
    public Dictionary<string, string> Variables { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<BatchConfig> Batches { get; set; } = [];
}

public sealed class BatchConfig
{
    public string Name { get; set; } = "Batch";
    public string Path { get; set; } = string.Empty;
    public bool PreserveContext { get; set; } = true;
    public string? Model { get; set; }
    public Dictionary<string, string> Variables { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ProjectConfig
{
    public string? ProjectName { get; set; }
    public string? PromptSet { get; set; }
    public Dictionary<string, string> Variables { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<ApplicationConfig> Applications { get; set; } = [];
}

public sealed class ApplicationConfig
{
    public string Name { get; set; } = string.Empty;
    public Dictionary<string, string> Variables { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class RunState
{
    public string RepositoryRoot { get; set; } = string.Empty;
    public string PromptSetName { get; set; } = string.Empty;
    public Dictionary<string, ApplicationRunState> Applications { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ApplicationRunState
{
    public Dictionary<string, BatchRunState> Batches { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class BatchRunState
{
    public string? SessionId { get; set; }
    public Dictionary<string, PromptRunState> Prompts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class PromptRunState
{
    public string Status { get; set; } = "pending";
    public int Attempts { get; set; }
    public DateTimeOffset? LastRunUtc { get; set; }
    public int? ExitCode { get; set; }
    public string? LogFile { get; set; }
    public string? Model { get; set; }
}

public sealed class CliOptions
{
    public string Command { get; set; } = "run";
    public string? Repository { get; set; }
    public string? PromptSet { get; set; }
    public string? Config { get; set; }
    public string? Application { get; set; }
    public string? Batch { get; set; }
    public string? Prompt { get; set; }
    public string? Model { get; set; }
    public bool Force { get; set; }
    public bool DryRun { get; set; }
    public bool RetryFailedOnly { get; set; }
    public Dictionary<string, string> Overrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
