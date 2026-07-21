using System;
using CairnMultiplayer.Api;
using MelonLoader;

namespace ManagedExtensionExample;

/// <summary>Minimal integration showing commands, retained state, events and safe game effects.</summary>
public sealed class ExampleExtensionMod : MelonMod
{
    private ReplicatedState<int> _teamScore;
    private MultiplayerEvent<ScoreEvent> _scoreEvent;
    private MultiplayerCommand<AddScoreRequest> _addScore;

    public override void OnInitializeMelon()
    {
        var extension = MultiplayerApi.RegisterExtension(new ExtensionRegistration(
            "com.example.team-score", new Version(1, 0, 0))
        {
            Requirement = ExtensionRequirement.Optional,
            MinimumPeerVersion = new Version(1, 0, 0),
            MaximumPeerVersion = new Version(1, 0, 99),
        });

        _teamScore = extension.RegisterState<int>("team-score");
        _scoreEvent = extension.RegisterEvent<ScoreEvent>("score-added");
        _addScore = extension.RegisterCommand<AddScoreRequest>("add-score", context =>
        {
            if (context.Request.Amount < 1 || context.Request.Amount > 10)
            {
                context.Reject("Amount must be between 1 and 10.");
                return;
            }

            _teamScore.TryGet(out var current);
            context.Set(_teamScore, current + context.Request.Amount);
            context.Broadcast(_scoreEvent, new ScoreEvent
            {
                PlayerId = context.Sender.Id,
                Amount = context.Request.Amount,
            });
            context.AfterCommit(() => MelonLogger.Msg("Score committed by the host."));
        });

        _teamScore.Changed += change =>
            MelonLogger.Msg(change.Removed ? "Team score cleared." : $"Team score: {change.Value}");
        _scoreEvent.Received += message =>
            MelonLogger.Msg($"Player {message.Payload.PlayerId} added {message.Payload.Amount} point(s).");
    }

    public async void RequestOnePoint()
    {
        var result = await _addScore.SendAsync(new AddScoreRequest { Amount = 1 });
        if (!result.Committed)
            MelonLogger.Warning($"Score request rejected: {result.Reason}");
    }
}

public sealed class AddScoreRequest
{
    public int Amount { get; set; }
}

public sealed class ScoreEvent
{
    public int PlayerId { get; set; }
    public int Amount { get; set; }
}
