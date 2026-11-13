using Sig2Email.Core.Models;

namespace Sig2Email.Core.Rendering;

public static class Placeholders
{
    public static readonly string[] UserFields =
    {
        "FirstName", "LastName", "DisplayName", "Email", "JobTitle", "Department", "Company", "Office",
        "Phone", "MobilePhone", "Fax", "Street", "City", "State", "PostalCode", "Country",
    };

    public static readonly string[] Special = { "Photo", "QrCode", "CurrentYear", "CurrentDate" };

    private static readonly HashSet<string> Known = Build();

    private static HashSet<string> Build()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in UserFields) set.Add(f);
        foreach (var f in Special) set.Add(f);
        for (var i = 1; i <= 15; i++) set.Add("CustomAttribute" + i);
        return set;
    }

    public static bool IsKnown(string name) => Known.Contains(name);

    public static IEnumerable<string> All => Known;

    /// <summary>Attribute values of a directory user, keyed by placeholder name (case-insensitive).</summary>
    public static Dictionary<string, string?> ForUser(DirectoryUser u)
    {
        var d = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["FirstName"] = u.FirstName,
            ["LastName"] = u.LastName,
            ["DisplayName"] = u.DisplayName,
            ["Email"] = u.PrimaryEmail,
            ["JobTitle"] = u.JobTitle,
            ["Department"] = u.Department,
            ["Company"] = u.Company,
            ["Office"] = u.Office,
            ["Phone"] = u.Phone,
            ["MobilePhone"] = u.MobilePhone,
            ["Fax"] = u.Fax,
            ["Street"] = u.Street,
            ["City"] = u.City,
            ["State"] = u.State,
            ["PostalCode"] = u.PostalCode,
            ["Country"] = u.Country,
        };
        for (var i = 1; i <= 15; i++)
            d["CustomAttribute" + i] = u.CustomAttributes.GetValueOrDefault(i.ToString());
        return d;
    }
}
