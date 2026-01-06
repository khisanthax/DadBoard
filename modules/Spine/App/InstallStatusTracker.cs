using System;

namespace DadBoard.App;

sealed class InstallStatusTracker
{
    private readonly string _path;
    private readonly InstallStatusSnapshot _snapshot;

    public InstallStatusTracker(string path)
    {
        _path = path;
        _snapshot = InstallStatusIo.Read(path) ?? InstallStatusFactory.CreateDefault();
        InstallStatusFactory.EnsureSteps(_snapshot);
        InstallStatusIo.Write(_path, _snapshot);
    }

    public InstallStatusSnapshot Snapshot => _snapshot;

    public void UpdateStep(string name, InstallStepStatus status, string? message)
    {
        var step = _snapshot.GetOrAddStep(name);
        step.Status = status;
        step.Message = message;
        InstallStatusIo.Write(_path, _snapshot);
    }

    public void Complete(bool success, string? errorMessage)
    {
        _snapshot.Completed = true;
        _snapshot.Success = success;
        _snapshot.ErrorMessage = errorMessage;
        InstallStatusIo.Write(_path, _snapshot);
    }
}
