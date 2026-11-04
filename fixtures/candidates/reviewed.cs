using System;

public static class Reviewed
{
    public static bool Accept(string input, bool eligible, bool member)
    {
        if (input == null) throw new ArgumentNullException(nameof(input));
        if (input.Length > 10) throw new ArgumentException("invalid input");
        if (input.Length == 0) return false;
        if (!eligible || !member) return false;
        Record(input);
        return true;
    }

    public static string Classify(int amount, bool member)
    {
        if (amount < 0) return "Invalid";
        if (member && amount >= 100) return "Gold";
        return "None";
    }

    public static extern void Record(string input);
}
