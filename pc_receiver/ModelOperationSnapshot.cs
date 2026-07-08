namespace pc_receiver;

public sealed record ModelOperationSnapshot(
    bool IsRunning,
    string Message,
    double Progress,
    bool IsIndeterminate);
