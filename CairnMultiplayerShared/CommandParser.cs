using System;

namespace CairnMultiplayer.Shared;

/// <summary>
/// Resultat du parsing d'une ligne de chat. <see cref="IsCommand"/> est vrai quand
/// la ligne commence par '/'. Le nom est normalise en minuscules pour un matching
/// insensible a la casse. <see cref="ArgsText"/> conserve tout le texte apres le nom
/// (utile pour cibler un joueur dont le pseudo contient des espaces).
/// </summary>
public readonly struct ParsedCommand
{
    public bool IsCommand { get; }
    public string Name { get; }
    public string[] Args { get; }
    public string ArgsText { get; }

    public ParsedCommand(bool isCommand, string name, string[] args, string argsText)
    {
        IsCommand = isCommand;
        Name = name;
        Args = args;
        ArgsText = argsText;
    }
}

/// <summary>
/// Parseur pur (sans dependance Unity/jeu, donc testable en xUnit) des lignes de chat
/// en commandes "/nom args".
/// </summary>
public static class CommandParser
{
    private static readonly char[] Whitespace = { ' ', '\t' };

    /// <summary>
    /// Parse une ligne de chat. Si elle ne commence pas par '/', retourne
    /// <c>IsCommand=false</c> (message normal). Sinon, decoupe "/nom arg1 arg2 ..." :
    /// le nom (minuscules, sans le '/'), les args separes par espaces, et le texte brut
    /// apres le nom.
    /// </summary>
    public static ParsedCommand Parse(string input)
    {
        var line = (input ?? "").Trim();
        if (line.Length == 0 || line[0] != '/')
            return new ParsedCommand(false, "", Array.Empty<string>(), "");

        // Retire le '/' initial.
        var body = line.Substring(1).Trim();
        if (body.Length == 0)
            return new ParsedCommand(true, "", Array.Empty<string>(), "");

        var parts = body.Split(Whitespace, StringSplitOptions.RemoveEmptyEntries);
        var name = parts[0].ToLowerInvariant();

        var args = parts.Length > 1 ? new string[parts.Length - 1] : Array.Empty<string>();
        for (int i = 1; i < parts.Length; i++)
            args[i - 1] = parts[i];

        // Texte apres le nom de commande, espaces de bord retires mais espaces
        // internes conserves (pseudos multi-mots).
        var nameLen = parts[0].Length;
        var argsText = body.Length > nameLen ? body.Substring(nameLen).Trim() : "";

        return new ParsedCommand(true, name, args, argsText);
    }
}
