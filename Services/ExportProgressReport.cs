namespace TelegaScan.Services;

public enum ExportWorkPhase
{
    Idle,
    History,
    Participants,
    Html,
    Media,
    Done
}

/// <summary>Снимок состояния экспорта для панели прогресса и журнала.</summary>
public sealed record ExportProgressReport(
    ExportWorkPhase Phase,
    string PhaseTitle,
    string? LogLine,
    int MessagesLoadedCount,
    int MediaProcessed,
    int MediaTotal,
    int Photos,
    int Videos,
    int Gifs,
    int Stickers,
    int Voices,
    int Docs,
    int Skipped,
    long BytesDownloaded,
    string? CurrentFileHint,
    int ParticipantStep,
    int ParticipantTotal,
    bool ProgressIndeterminate,
    double ProgressFraction);
