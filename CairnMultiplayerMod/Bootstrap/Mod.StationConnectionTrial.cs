using System;
using CairnMultiplayerMod.GameApi;
using UnityEngine;

namespace CairnMultiplayerMod.Bootstrap;

public partial class Mod
{
    private GUIStyle _stationConnectionStyle;

    private void DrawStationConnectionTrial()
    {
        if (Network?.IsConnected == true || _multiplayerModeActive) return;
        var status = _game.Belay.StationConnectionStatus;
        if (!_game.State.IsLocalPlayerInGame && !status.HasValue) return;
        _stationConnectionStyle ??= new GUIStyle(GUI.skin.box)
        {
            fontSize = 18, wordWrap = true, alignment = TextAnchor.UpperLeft,
            padding = new RectOffset(14, 14, 12, 12)
        };
        var passed = status?.Stage == StationConnectionStage.Passed;
        var failed = status?.Stage == StationConnectionStage.Failed;
        var title = passed ? TrialText("PASS — STATION RECONNECTION", "APROVADO — RECONEXÃO DA ESTAÇÃO")
            : failed ? TrialText("NOT APPROVED — STATION CONNECTION", "NÃO APROVADO — CONEXÃO DA ESTAÇÃO")
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
        var footer = failed
            ? TrialText("Keep B attached. Ctrl+F12 retries only if the original anchors and route are still valid. No automatic rollback is performed.",
                "Mantenha B preso. Ctrl+F12 tenta novamente somente se as ancoragens e o percurso originais ainda forem válidos. O teste não desfaz alterações automaticamente.")
            : passed ? TrialText("Saved result. A new attempt or station action replaces it.", "Resultado guardado. Uma nova tentativa ou ação de estação substitui este resultado.")
                : TrialText("Only two pitons. No falls, Ctrl+F11, pause or window switching during this round.",
                    "Somente dois pitons. Sem quedas, Ctrl+F11, pausa ou troca de janela durante esta rodada.");
        var previous = GUI.contentColor;
        try
        {
            GUI.contentColor = passed ? new Color(.4f, 1, .5f, 1) : failed ? new Color(1, .55f, .4f, 1) : Color.white;
            GUI.Box(new Rect(20, 20, Math.Min(840f, Screen.width - 40f), 300),
                title + "\n" + TrialText("Piton A: station | Piton B: your personal anchor", "Piton A: estação | Piton B: sua ancoragem pessoal")
                    + "\n\n" + instruction + "\n\n" + footer, _stationConnectionStyle);
        }
        finally { GUI.contentColor = previous; }
    }

    private static string StationConnectionFailureText(StationConnectionFailure failure) => failure switch
    {
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
