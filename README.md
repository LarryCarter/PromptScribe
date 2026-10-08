# PromptScribe

PromptScribe is a reusable .NET 10 console application that runs ordered batches of already-tested prompts through GitHub Copilot CLI against the repository currently open in Visual Studio or VS Code.

PromptScribe intentionally does not interpret or reshape prompt output. Your prompts decide what to inspect, which Markdown/JSON files to create or append, and how the documentation is structured. PromptScribe only handles execution.

## Core behavior

- Uses the current working directory / Git root as the default project scope.
- Supports one application or many applications in the same repository.
- Replaces arbitrary `{{VARIABLE_NAME}}` tokens in each prompt.
- Runs all configured batches and prompts in alphabetical order.
- Preserves a dedicated Copilot CLI session for each Application + Batch when `preserveContext` is true.
- Supports default, batch, or command-line model selection.
- Stores execution state and logs outside the repository under `%LOCALAPPDATA%\PromptScribe`.
- Can resume, rerun individual prompts, retry failures, or show status.
- Allows Copilot file-write tools because the prompts need to create documentation, while explicitly denying common Git mutation commands and excluding Copilot web search/fetch tools.

## Requirements

- Windows with .NET 10 SDK to build.
- GitHub Copilot CLI installed, authenticated, and permitted by your organization.
- `git` is recommended for automatic repository-root detection.

## Build and install globally

From the PromptScribe source folder:

```powershell
.\publish.ps1
.\install.ps1
```

Open a new Visual Studio or VS Code terminal, then verify:

```powershell
PromptScribe doctor
```

For a self-contained executable:

```powershell
.\publish.ps1 -SelfContained
.\install.ps1
```

## Put in the real prompts

Copy the already-tested prompt files into:

```text
promptsets\legacy-analysis\01-discovery\   # prompts 01-10
promptsets\legacy-analysis\02-expansion\   # prompts 11-20
```

PromptScribe executes `*.md` alphabetically. Prefixing filenames with `01-`, `02-`, etc. guarantees order.

## Variables

Any token in the form below is replaced before a prompt is sent to Copilot:

```text
{{APPLICATION_NAME}}
{{SOME_OTHER_VARIABLE}}
```

Built-in variables:

- `APPLICATION_NAME`
- `PROJECT_NAME`
- `REPOSITORY_ROOT`
- `SOLUTION_NAME`
- `PROMPT_SET`
- `BATCH_NAME`

Additional variables can come from the prompt set, project config, batch config, application config, or the command line.

Precedence is effectively: prompt set -> project -> batch -> application -> built-ins -> command-line `--set` overrides.

Example:

```powershell
PromptScribe run --set ENVIRONMENT=Legacy --set DOCUMENT_TYPE=Architecture
```

## Multi-application repository

Create `.promptscribe.json` at the repository root. See `examples/.promptscribe.multi-app.json`.

Example:

```json
{
  "projectName": "LegacyPlatform",
  "promptSet": "legacy-analysis",
  "applications": [
    { "name": "AccountOnboarding", "variables": {} },
    { "name": "Payments", "variables": {} }
  ]
}
```

PromptScribe automatically sets `APPLICATION_NAME` to each application's name unless overridden from the command line.

If no `.promptscribe.json` exists, PromptScribe treats the current repository as a single application and derives its name from the top-level solution or repository folder.

## Model selection

A default model can be specified in `promptset.json`:

```json
"defaultModel": "gpt-5.4"
```

A batch can override it:

```json
{
  "name": "Discovery",
  "path": "01-discovery",
  "preserveContext": true,
  "model": "claude-sonnet-4.5"
}
```

A run can override all configuration:

```powershell
PromptScribe run --model gpt-5.4
```

Leaving models `null` lets Copilot use its configured/default model.

## Commands

```powershell
PromptScribe run
PromptScribe resume
PromptScribe retry-failed
PromptScribe status
PromptScribe list
PromptScribe doctor
```

Run one application:

```powershell
PromptScribe run --application AccountOnboarding
```

Run one batch:

```powershell
PromptScribe run --application AccountOnboarding --batch Discovery
```

Run/rerun one prompt:

```powershell
PromptScribe run --application AccountOnboarding --batch Discovery --prompt 07 --force
```

The `--prompt` value can be the full filename or filename stem. If your file is `07-dependencies.md`, use:

```powershell
PromptScribe run --prompt 07-dependencies --force
```

Preview without invoking Copilot:

```powershell
PromptScribe run --dry-run
```

Explicit repo/config/prompt set overrides:

```powershell
PromptScribe run --repository C:\Repos\LegacyPlatform
PromptScribe run --config C:\Config\legacy.promptscribe.json
PromptScribe run --promptset C:\PromptSets\legacy-analysis
```

## Context behavior

Every application/batch pair gets a deterministic saved session ID in PromptScribe state.

For the legacy prompt set this means:

```text
Application A
  Discovery (01-10) -> one growing Copilot session
  Expansion (11-20) -> one growing Copilot session

Application B
  Discovery (01-10) -> different session
  Expansion (11-20) -> different session
```

This gives the first ten prompts the growing context you wanted, lets the second ten grow together while operating on the documentation created earlier, and prevents context from leaking between applications.

## State and logs

PromptScribe writes runtime state outside the code repository:

```text
%LOCALAPPDATA%\PromptScribe\state
%LOCALAPPDATA%\PromptScribe\logs
```

This keeps PromptScribe execution metadata from appearing in the application source repository unless you deliberately add a `.promptscribe.json` project configuration there.

## Important safety behavior

PromptScribe gives Copilot permission to use its direct file-write tool because your prompts intentionally write documentation. It does not grant unrestricted shell access. It also denies common Git mutation commands such as `git push`, `git commit`, `git checkout`, `git reset`, and `git clean`, and it excludes Copilot web-search/web-fetch tools.

PromptScribe takes a `git status --porcelain` snapshot before and after execution and warns if repository files changed. Documentation changes are expected; always review Git status before committing anything.
