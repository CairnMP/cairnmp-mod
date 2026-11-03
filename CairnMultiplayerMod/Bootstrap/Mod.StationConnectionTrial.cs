using System;
using CairnMultiplayerMod.GameApi;
using UnityEngine;

namespace CairnMultiplayerMod.Bootstrap;

public partial class Mod
{
    private GUIStyle _stationConnectionStyle;
    private bool _stationConnectionUiShown;

    private void DrawStationConnectionTrial()
    {
        if (Network?.IsConnected == true || _multiplayerModeActive) return;
        var status = _game.Belay.StationConnectionStatus;
        if (!_game.Belay.StationConnectionContext.ShowPanel(_game.Belay.LocalStationStatus.HasValue, status.HasValue))
        { _stationConnectionUiShown = false; return; }
        if (!_stationConnectionUiShown)
            LoggerInstance.Msg("[StationConnection] Panel draw started; nativeInGame=" + _game.Belay.StationConnectionContext.NativeInGame
                + ", station=" + _game.Belay.LocalStationStatus.HasValue + ", result=" + status.HasValue);
        _stationConnectionStyle ??= new GUIStyle(GUI.skin.box)
        {
            fontSize = 18, wordWrap = true, alignment = TextAnchor.UpperLeft,
            padding = new RectOffset(14, 14, 12, 12)
        };
        var passed = status?.Stage == StationConnectionStage.Passed;
        var failed = status?.Stage == StationConnectionStage.Failed;
        var notReady = status?.Stage == StationConnectionStage.NotReady;
        var title = passed ? TrialText("PASS — STATION RECONNECTION", "APROVADO — RECONEXÃO DA ESTAÇÃO")
            : failed ? TrialText("NOT APPROVED — STATION CONNECTION", "NÃO APROVADO — CONEXÃO DA ESTAÇÃO")
                : notReady ? TrialText("NOT STARTED — CHECK THE CURRENT STEP", "NÃO INICIADO — CONFIRA A ETAPA ATUAL")
                : TrialText("SOLO STATION CONNECTION — FOLLOW THE CURRENT STEP", "CONEXÃO SOLO DA ESTAÇÃO — SIGA A ETAPA ATUAL");
        var instruction = !status.HasValue
            ? _game.Belay.LocalStationStatus.HasValue
                ? TrialText("A recorded. Release your personal anchor, climb about 2 m, place B and anchor yourself at B. Then press Ctrl+F12 once.",
                    "A registrado. Solte sua ancoragem pessoal, suba cerca de 2 m, coloque B e prenda-se em B. Então pressione Ctrl+F12 uma vez.")
                : TrialText("Place A, anchor yourself on A and press Ctrl+F10 once. A and B are piton labels, not keyboard keys.",
                    "Coloque A, prenda-se em A e pressione Ctrl+F10 uma vez. A e B são nomes dos pitons, não teclas.")
            : status.Value.Stage switch
            {
                StationConnectionStage.Unclipping => TrialText("Wait: the game is unclipping only the rope from A. Remain personally anchored at B.",
                    "Aguarde: o jogo está soltando somente a corda de A. Continue pessoalmente preso em B."),
                StationConnectionStage.Detached => TrialText("A is confirmed OFF the rope; B remains attached. Press Ctrl+F12 once to request reconnection of A.",
                    "CONFIRMADO: A está FORA da corda; B continua conectado. Pressione Ctrl+F12 uma vez para reconectar A."),
                StationConnectionStage.Connecting => TrialText("Connection requested. Wait for the native A -> B route; remain anchored at B.",
                    "Conexão solicitada. Aguarde o percurso A → B ser confirmado pelo jogo; continue preso em B."),
                StationConnectionStage.Confirming => TrialText($"Native connection confirmed. Keep B attached and remain still: {status.Value.ConfirmedSeconds:F1} / 5.0 s.",
                    $"Conexão confirmada pelo jogo. Continue preso em B e parado: {status.Value.ConfirmedSeconds:F1} / 5,0 s."),
                StationConnectionStage.Passed => TrialText("This round passed: A was observed off the rope and then on the A -> B route for five seconds, with personal anchor B retained. Report the result; do not make a fall.",
                    "Esta rodada passou: A foi observado fora da corda e depois no percurso A → B por cinco segundos, mantendo a ancoragem pessoal B. Avise o resultado; não faça uma queda."),
                _ => StationConnectionFailureText(status.Value.Failure)
            };
        var footer = notReady
            ? TrialText("No unclip/connection request was issued in this attempt. Keep B attached. The specific check is recorded in the log.",
                "Nenhum pedido para soltar/conectar a corda foi enviado nesta tentativa. Mantenha B preso. A checagem está registrada no log.")
            : failed
            ? TrialText("Keep B attached. Ctrl+F12 retries only if the original anchors and route are still valid. No automatic rollback is performed.",
                "Mantenha B preso. Ctrl+F12 tenta novamente somente se as ancoragens e o percurso originais ainda forem válidos. O teste não desfaz alterações automaticamente.")
            : passed ? TrialText("Saved result. A new attempt or station action replaces it.", "Resultado guardado. Uma nova tentativa ou ação de estação substitui este resultado.")
                : TrialText("Only two pitons. No falls, Ctrl+F11, pause or window switching during this round.",
                    "Somente dois pitons. Sem quedas, Ctrl+F11, pausa ou troca de janela durante esta rodada.");
        var previous = GUI.contentColor;
        try
        {
            GUI.contentColor = passed ? new Color(.4f, 1, .5f, 1) : failed ? new Color(1, .55f, .4f, 1)
                : notReady ? new Color(1, .7f, .4f, 1) : Color.white;
            GUI.Box(new Rect(20, 20, Math.Min(840f, Screen.width - 40f), Math.Min(failed || notReady ? 400f : 300f, Screen.height - 40f)),
                title + "\n" + TrialText("Piton A: station | Piton B: your personal anchor", "Piton A: estação | Piton B: sua ancoragem pessoal")
                    + "\n\n" + instruction
                    + (failed || notReady ? "\n" + TrialText("Check: ", "Leitura: ") + status.Value.Detail : "")
                    + "\n\n" + footer, _stationConnectionStyle);
            if (!_stationConnectionUiShown)
            { _stationConnectionUiShown = true; LoggerInstance.Msg("[StationConnection] Panel draw completed."); }
        }
        finally { GUI.contentColor = previous; }
    }

    private static string StationConnectionFailureText(StationConnectionFailure failure) => failure switch
    {
        StationConnectionFailure.StationRequired => TrialText("A has not been recorded. Return to A, personally anchor there and press Ctrl+F10 once before preparing B.",
            "A ainda não foi registrada. Volte a A, prenda-se nela e pressione Ctrl+F10 uma vez antes de preparar B."),
        StationConnectionFailure.BackupRequired => TrialText("Personally anchor at a healthy B, different from A and at most 6 m away. Remain still and press Ctrl+F12 again.",
            "Prenda-se pessoalmente em B saudável, diferente de A e a até 6 m dela. Fique parado e pressione Ctrl+F12 novamente."),
        StationConnectionFailure.RouteNotReady => TrialText("Keep exactly A then B connected to the rope. Wait for the route to settle before Ctrl+F12 again.",
            "Mantenha somente A e depois B conectados à corda. Aguarde o percurso estabilizar antes de outro Ctrl+F12."),
        StationConnectionFailure.NativePartsNotReady => TrialText("A native rope segment's physics is not ready. Keep B attached and wait two seconds before one Ctrl+F12 retry. If this repeats, report it for log analysis.",
            "A física de um trecho da corda não está pronta. Mantenha B preso e espere dois segundos antes de repetir Ctrl+F12 uma vez. Se persistir, avise para análise do log."),
        StationConnectionFailure.NativePinsMismatch => TrialText("The physical rope connections do not match the expected A/B quickdraws. Keep B attached and report this result for log analysis.",
            "As conexões físicas da corda não correspondem ao esperado em A/B. Mantenha B preso e avise este resultado para análise do log."),
        StationConnectionFailure.Context => TrialText("The round stopped after focus, pause, mode or scene changed.", "A rodada parou após mudança de foco, pausa, modo ou cena."),
        StationConnectionFailure.BackupLost => TrialText("B stopped being the same healthy personal anchor, or the character moved. No further rope requests were issued.",
            "B deixou de ser a mesma ancoragem pessoal saudável, ou o personagem se moveu. Nenhum novo comando de conexão foi enviado."),
        StationConnectionFailure.StationLost => TrialText("Recorded A was lost, moved, broken or replaced.", "A estação A registrada foi perdida, movida, quebrada ou substituída."),
        StationConnectionFailure.RouteChanged => TrialText("The route changed or did not return to the expected A -> B order. This round did not pass.",
            "O percurso mudou ou não voltou à ordem esperada A → B. Esta rodada não foi aprovada."),
        StationConnectionFailure.Timeout => TrialText("The native transition did not stabilize in time. Keep B attached before retrying.",
            "A alteração do jogo não estabilizou no prazo. Mantenha B preso antes de tentar novamente."),
        StationConnectionFailure.NativeRejected => TrialText("The game refused the request or returned an error. Keep B attached; the log records the reason.",
            "O jogo recusou o pedido ou retornou um erro. Mantenha B preso; o motivo foi registrado no log."),
        _ => TrialText("Setup not ready. Record healthy A with Ctrl+F10, then place and personally anchor at distinct B. Keep exactly A -> B on the rope before Ctrl+F12.",
            "Preparação incompleta. Registre A saudável com Ctrl+F10; depois coloque outro piton B e prenda-se nele. Mantenha somente A → B na corda antes de Ctrl+F12.")
    };
}
