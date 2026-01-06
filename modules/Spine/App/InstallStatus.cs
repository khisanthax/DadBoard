using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using DadBoard.Spine.Shared;

namespace DadBoard.App;

enum InstallStepStatus
{
    Pending,
    Running,
    Success,
    Failed
}

sealed class InstallStepState
{
    public string Name { get; set; } = "";
    public InstallStepStatus Status { get; set; } = InstallStepStatus.Pending;
    public string? Message { get; set; }
}

sealed class InstallStatusSnapshot
{
    public bool Completed { get; set; }
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public List<InstallStepState> Steps { get; set; } = new();

    public InstallStepState GetOrAddStep(string name)
    {
        var step = Steps.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
        if (step == null)
        {
            step = new InstallStepState { Name = name };
            Steps.Add(step);
        }

        return step;
    }
}

static class InstallSteps
{
    public const string Elevate = "Elevate (UAC)";
    public const string CopyExe = "Copy DadBoard.exe";
    public const string CreateData = "Create ProgramData folders/configs";
    public const string CreateTask = "Create/Update Scheduled Task";
    public const string Firewall = "Add firewall rules";
    public const string Launch = "Launch installed copy";

    public static readonly string[] Ordered =
    {
        Elevate,
        CopyExe,
        CreateData,
        CreateTask,
        Firewall,
        Launch
    };
}

static class InstallStatusFactory
{
    public static InstallStatusSnapshot CreateDefault()
    {
        var snapshot = new InstallStatusSnapshot();
        foreach (var step in InstallSteps.Ordered)
        {
            snapshot.Steps.Add(new InstallStepState { Name = step });
        }

        return snapshot;
    }

    public static InstallStatusSnapshot EnsureSteps(InstallStatusSnapshot snapshot)
    {
        foreach (var step in InstallSteps.Ordered)
        {
            snapshot.GetOrAddStep(step);
        }

        return snapshot;
    }
}

static class InstallStatusIo
{
    public static InstallStatusSnapshot? Read(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<InstallStatusSnapshot>(json, JsonUtil.Options);
        }
        catch
        {
            return null;
        }
    }

    public static void Write(string path, InstallStatusSnapshot snapshot)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var json = JsonSerializer.Serialize(snapshot, JsonUtil.Options);
        File.WriteAllText(path, json);
    }
}
