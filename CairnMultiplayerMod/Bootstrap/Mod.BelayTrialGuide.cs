using System;
using CairnMultiplayerMod.Internal;
using CairnMultiplayerMod.Internal.Diagnostics;
using UnityEngine;

namespace CairnMultiplayerMod.Bootstrap;

public partial class Mod
{
    private readonly BelayAppendTrialTracker _localBelayAppendTrial = new();
    private GUIStyle _localBelayAppendStyle;
    private BelayAppendTrialStage? _lastBelayTrialStage;
    private BelayAppendTrialFailure _lastBelayTrialFailure;
    private double _nextBelayTrialLog;
    private static bool LocalAppendGuideEnabled => ModConfig.BelayRopeExperiment?.Value == true
        && ModConfig.BelayAppendGuide?.Value == true && ModConfig.BelayStationConnectionExperiment?.Value != true;
    private static string TrialText(string english, string portuguese)
        => string.Equals(ModConfig.BelayAppendGuideLanguage?.Value, "pt", StringComparison.OrdinalIgnoreCase)
            ? portuguese : english;

    private void TickBelayAppendGuide()
    {
        if (!LocalAppendGuideEnabled || _game == null) return;
        var status = _game.Belay.Status;
        var station = _game.Belay.LocalStationStatus;
        var rope = _game.Belay.LocalRopeStatus;
        var sample = new BelayAppendTrialSample(_game.Belay.IsLocalExercise && status.Active,
            station.HasValue, station?.Valid == true, station?.IntermediateProtections ?? 0,
            status.TopologyRevision, status.Rebinding, rope?.Braked ?? status.RequestedBrake,
            status.Suspended, rope.HasValue, rope?.Slack ?? 0, rope?.Motion ?? 0,
            rope?.LengthApplied == true, Application.isFocused && Time.timeScale > 0
                && Network?.IsConnected != true && !_multiplayerModeActive);
        var now = Time.realtimeSinceStartupAsDouble;
        _localBelayAppendTrial.Observe(sample, now);
        if (_lastBelayTrialStage == _localBelayAppendTrial.Stage
            && _lastBelayTrialFailure == _localBelayAppendTrial.Failure) return;
        if (!_localBelayAppendTrial.HasOutcome && now < _nextBelayTrialLog) return;
        _lastBelayTrialStage = _localBelayAppendTrial.Stage;
        _lastBelayTrialFailure = _localBelayAppendTrial.Failure;
        _nextBelayTrialLog = now + .5;
        LoggerInstance.Msg("[BelayTrial] attempt=" + _localBelayAppendTrial.Attempt
            + ", stage=" + _localBelayAppendTrial.Stage + ", failure=" + _localBelayAppendTrial.Failure
            + ", active=" + sample.Active + ", station=" + sample.HasStation
            + ", protections=" + sample.Intermediates + ", brake=" + sample.Braked
            + ", revision=" + sample.Revision + ", language=" + ModConfig.BelayAppendGuideLanguage?.Value);
    }

    private void DrawBelayAppendGuide()
    {
        if (Network?.IsConnected == true || _multiplayerModeActive) return;
        if (!_game.State.IsLocalPlayerInGame && !_localBelayAppendTrial.HasOutcome
            && !_game.Belay.LocalStationStatus.HasValue) return;
        _localBelayAppendStyle ??= new GUIStyle(GUI.skin.box)
        {
            fontSize = 18, wordWrap = true, alignment = TextAnchor.UpperLeft,
            padding = new RectOffset(14, 14, 12, 12)
        };
        var stage = _localBelayAppendTrial.Stage;
        var active = _game.Belay.IsLocalExercise && _game.Belay.Status.Active;
        var heading = stage == BelayAppendTrialStage.Passed ? TrialText("PASS — LOCAL BRAKED ADDITION", "APROVADO — ADIÇÃO LOCAL COM FREIO")
            : stage == BelayAppendTrialStage.Retry
                ? active ? TrialText("RETRY NEEDED — TEST INVALID", "PRECISA REFAZER — TESTE INVÁLIDO")
                    : TrialText("STOPPED — RETRY NEEDED", "PAROU — PRECISA REFAZER")
                : TrialText("LOCAL BRAKED ADDITION — FOLLOW THE CURRENT STEP", "TESTE LOCAL COM FREIO — SIGA A ETAPA ATUAL");
        var control = active ? TrialText("Rope control: ACTIVE", "Controle da corda: ATIVO")
            : TrialText("Rope control: STOPPED", "Controle da corda: PARADO");
        var detail = active
            ? (_game.Belay.Status.RequestedBrake ? " | BRAKED" : " | FREE")
                + TrialText($" | Intermediate protections: {_localBelayAppendTrial.Intermediates}",
                    $" | Proteções intermediárias: {_localBelayAppendTrial.Intermediates}") : "";
        var footer = stage == BelayAppendTrialStage.Passed
            ? active ? TrialText("This local trial passed. Ctrl+F11 ends control; the result stays visible.",
                    "Esta rodada local passou. Ctrl+F11 encerra o controle; o resultado permanece visível.")
                : TrialText("Control has ended. This result remains visible until a new attempt.",
                    "O controle foi encerrado. Este resultado permanece visível até uma nova tentativa.")
            : stage == BelayAppendTrialStage.Retry
                ? active ? TrialText("Press Ctrl+F11 to end control before preparing another attempt.",
                        "Pressione Ctrl+F11 para encerrar o controle antes de preparar outra tentativa.")
                    : TrialText("Prepare a new station and start with only B after it. Follow the station-recording confirmation.",
                        "Prepare uma nova estação e comece apenas com B depois dela. Confira a confirmação do registro da estação.")
                : TrialText("No falls. Keep the game focused and follow this panel until the final result.",
                    "Sem quedas. Mantenha o jogo em primeiro plano e siga o painel até o resultado final.");
        var previousContent = GUI.contentColor;
        try
        {
            GUI.contentColor = stage == BelayAppendTrialStage.Passed ? new Color(.4f, 1, .5f, 1)
                : stage == BelayAppendTrialStage.Retry ? new Color(1, .55f, .4f, 1) : Color.white;
            GUI.Box(new Rect(20, 20, Math.Min(840f, Screen.width - 40f), 280),
                heading + "\n" + control + detail + "\n\n" + BelayTrialInstruction()
                    + "\n\n" + footer, _localBelayAppendStyle);
        }
        finally { GUI.contentColor = previousContent; }
    }

    private string BelayTrialInstruction()
    {
        var trial = _localBelayAppendTrial;
        if (trial.Stage == BelayAppendTrialStage.Retry) return BelayTrialFailureText();
        return trial.Stage switch
        {
            BelayAppendTrialStage.RecordStation => TrialText(
                "STEP 1/6: Place piton A and anchor yourself to it. Press Ctrl+F10 and wait for station confirmation.",
                "ETAPA 1/6: Coloque o piton A e ancore-se nele. Pressione Ctrl+F10 e espere a confirmação da estação."),
            BelayAppendTrialStage.StartExercise => TrialText(
                "STEP 2/6: Station recorded. Release your personal anchor, climb about 2 m and place B. Stable on the wall, Ctrl+F11 starts.",
                "ETAPA 2/6: Estação registrada. Solte sua ancoragem, suba cerca de 2 m e coloque B. Firme na parede, Ctrl+F11 inicia."),
            BelayAppendTrialStage.FeedSlack => TrialText(
                $"STEP 3/6: Hold Up to feed rope. Slack: {trial.Slack:F1} / {BelayAppendTrialTracker.RequiredSlack:F1} m. Keep FREE.",
                $"ETAPA 3/6: Segure a SETA PARA CIMA para dar corda. Folga: {trial.Slack:F1} / {BelayAppendTrialTracker.RequiredSlack:F1} m. Mantenha FREE."),
            BelayAppendTrialStage.WaitForLength => TrialText(
                "RELEASE THE ARROWS AND WAIT: checking that the rope reached its requested length. Stay stable on the wall.",
                "SOLTE AS SETAS E AGUARDE: conferindo se a corda atingiu o comprimento pedido. Fique firme na parede."),
            BelayAppendTrialStage.CloseBrake => TrialText(
                "STEP 4/6: ENOUGH ROPE. Press Space once to close the brake; wait for BRAKED before climbing.",
                "ETAPA 4/6: CORDA SUFICIENTE. Pressione ESPAÇO uma vez para fechar o freio; espere BRAKED antes de subir."),
            BelayAppendTrialStage.OpenBrake => TrialText(
                "NOT READY: brake closed before enough slack was confirmed. Press Space for FREE, then follow the feed step.",
                "AINDA NÃO ESTÁ PRONTO: o freio fechou antes de confirmar folga suficiente. Pressione ESPAÇO para FREE e siga a etapa de dar corda."),
            BelayAppendTrialStage.AddProtection => TrialText(
                "STEP 5/6: BRAKED CONFIRMED. Climb only 1–2 m and place piton C. Do not use arrows or change the brake.",
                "ETAPA 5/6: BRAKED CONFIRMADO. Suba apenas 1–2 m e coloque o piton C. Não use as setas nem altere o freio."),
            BelayAppendTrialStage.UpdatingRoute => TrialText(
                "UPDATING THE ROPE: piton placement is in progress. Stay stable and wait; keep BRAKED.",
                "ATUALIZANDO A CORDA: a colocação do piton está em andamento. Fique firme e aguarde; mantenha BRAKED."),
            BelayAppendTrialStage.HoldStill => TrialText(
                $"STEP 6/6: TWO PROTECTIONS CONFIRMED. Stay stable for {trial.RemainingSeconds:F1} s. Keep BRAKED and wait for PASS.",
                $"ETAPA 6/6: DUAS PROTEÇÕES CONFIRMADAS. Fique firme por mais {trial.RemainingSeconds:F1} s. Mantenha BRAKED e espere APROVADO."),
            BelayAppendTrialStage.Passed => TrialText(
                "The addition stayed active and BRAKED with two intermediate protections for 5 seconds. You may return to chat.",
                "A adição permaneceu ativa e em BRAKED com duas proteções intermediárias por 5 segundos. Pode voltar ao chat."),
            _ => ""
        };
    }

    private string BelayTrialFailureText()
        => _localBelayAppendTrial.Failure switch
        {
            BelayAppendTrialFailure.MissingStation => TrialText("Station missing. This started the ordinary rope exercise, not the station trial. End it and record A first.",
                "Faltou registrar a estação. Foi iniciado o ensaio comum. Encerre-o e registre A antes de tentar novamente."),
            BelayAppendTrialFailure.StationCleared => TrialText("Station cleared. While anchored to A, press Ctrl+F10 again to record it; wait for STEP 2.",
                "A estação foi APAGADA. Ancorado em A, pressione Ctrl+F10 novamente para registrá-la; espere a ETAPA 2."),
            BelayAppendTrialFailure.WrongStartRoute => TrialText("Wrong starting route. Record A and place only B after it before starting Ctrl+F11.",
                "Percurso inicial diferente do teste. Registre A e coloque apenas B depois dele antes de iniciar Ctrl+F11."),
            BelayAppendTrialFailure.UnpreparedAppend => TrialText("A piton was added before the BRAKED preparation was confirmed. Feed enough rope and close the brake BEFORE adding C.",
                "Um piton foi adicionado antes de confirmar a preparação em BRAKED. Libere corda suficiente e feche o freio ANTES de colocar C."),
            BelayAppendTrialFailure.BrakeOpened => TrialText("The brake opened during the addition or final observation. Repeat while keeping BRAKED.",
                "O freio abriu durante a adição ou a observação final. Refaça mantendo BRAKED."),
            BelayAppendTrialFailure.RopeInput => TrialText("An arrow command changed rope control after BRAKED was prepared. Repeat without arrow commands after closing the brake.",
                "Uma seta foi usada depois da preparação em BRAKED. Refaça sem usar as setas após fechar o freio."),
            BelayAppendTrialFailure.ExtraProtection => TrialText("The route changed beyond this trial's one added piton. Prepare A, B and then only C.",
                "O percurso mudou além do único piton previsto nesta rodada. Prepare A, B e depois apenas C."),
            BelayAppendTrialFailure.Suspended => TrialText("A suspension occurred. This trial requires wall climbing without a fall; prepare it again.",
                "O personagem ficou suspenso pela corda. Esta rodada exige ficar na parede, sem queda; prepare novamente."),
            BelayAppendTrialFailure.ContextLost => TrialText("The game lost focus or paused before completion. Prepare again and stay in the game until the result.",
                "O jogo perdeu o foco ou foi pausado antes de concluir. Refaça mantendo o jogo aberto até o resultado."),
            BelayAppendTrialFailure.InvalidMeasurement => TrialText("The observation was interrupted or a rope measurement was invalid. This attempt cannot be approved.",
                "A observação foi interrompida ou uma medida da corda ficou inválida. Esta tentativa não pode ser aprovada."),
            BelayAppendTrialFailure.RejectedAction => TrialText("The last command was not accepted. Check anchoring for Ctrl+F10, or end the active exercise before changing the station.",
                "O último comando não foi aceito. Confira a ancoragem para Ctrl+F10, ou encerre o ensaio ativo antes de alterar a estação."),
            _ => BelayTrialEndReason()
        };

    private string BelayTrialEndReason()
    {
        var reason = _game.Belay.LocalExerciseNotice ?? _localBelayNotice ?? "";
        if (reason.Contains("paid-out rope cannot cover", StringComparison.Ordinal))
            return TrialText("There was not enough released rope for the new piton. Repeat and wait for ENOUGH ROPE before closing the brake.",
                "A corda liberada não cobriu o novo piton. Refaça e espere CORDA SUFICIENTE antes de fechar o freio.");
        if (reason.Contains("toggled off", StringComparison.Ordinal))
            return TrialText("The exercise was ended before the 5-second confirmation finished. Wait for PASS before Ctrl+F11.",
                "O ensaio foi encerrado antes de terminar a confirmação de 5 segundos. Espere APROVADO antes de Ctrl+F11.");
        if (reason.Contains("focused", StringComparison.Ordinal) || reason.Contains("paused", StringComparison.Ordinal))
            return TrialText("The game lost focus or paused before completion. Repeat and stay in the game until the result.",
                "O jogo perdeu o foco ou foi pausado antes de concluir. Refaça mantendo o jogo em primeiro plano até o resultado.");
        if (reason.Contains("did not stabilize", StringComparison.Ordinal))
            return TrialText("The rope update did not finish in time. Report this retained result; do not repeat blindly.",
                "A atualização da corda não terminou a tempo. Avise este resultado; não precisa repetir sem analisarmos.");
        return TrialText("The controller ended before completion. Original reason: ",
            "O controle encerrou antes da conclusão. Motivo original: ") + reason;
    }
}
