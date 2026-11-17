using System.Text.Json;

namespace CapabilityExplorer.Explorer;

public static partial class ProcessLandscapeValidation
{
    private static readonly string[] NavigatorEventRoles = ["throw", "catch", "ref"];
    private static readonly string[] NavigatorNullableElementFields = ["pr", "lane", "st", "src", "dst", "msg"];
    private static readonly string[] NavigatorElementAssociations = ["in", "out"];

    // Source shapes: nav_data.py / nav_meta.py / build_navigator.py. Check only known structural
    // fields, preserving optional records and additive metadata verbatim. File references are bundle-local
    // names, not necessarily manifest paths; absence from another category does not make them invalid.
    private static void ValidateNavigatorCategories(JsonElement navigator)
    {
        foreach (var category in navigator.EnumerateObject())
        {
            switch (category.Name)
            {
                case "catalogue":
                    NavigatorRecords(category.Value, row =>
                    {
                        NavigatorStrings(row, "pid", "group", "name", "phases", "ctx", "inc", "grain", "proc", "purpose");
                        NavigatorFile(row, "file");
                        NavigatorOptional(row, "procs", NavigatorStringArray);
                    });
                    break;
                case "elements":
                case "links":
                    NavigatorMap(category.Value, byFile =>
                        NavigatorMap(byFile, row => ValidateNavigatorMetadata(row, category.Name == "elements")), fileKeys: true);
                    break;
                case "events":
                    NavigatorMap(category.Value, row =>
                    {
                        NavigatorKind(row, JsonValueKind.Object);
                        NavigatorStrings(row, "ctx", "kind", "agg", "phase");
                        foreach (var role in NavigatorEventRoles)
                            NavigatorOptional(row, role, refs => NavigatorTuples(refs, 2, fileReference: true));
                    });
                    break;
                case "lifecycle":
                    NavigatorRecords(category.Value, row =>
                    {
                        NavigatorStrings(row, "name", "entity", "obj", "ctx");
                        NavigatorOptional(row, "states", NavigatorStringArray);
                        NavigatorOptional(row, "tr", transitions => NavigatorRecords(transitions,
                            transition => NavigatorStrings(transition, "f", "t", "cmd", "ev", "guard", "dmn")));
                        NavigatorOptional(row, "set", states => NavigatorMap(states,
                            refs => NavigatorTuples(refs, 3, fileReference: true)));
                    });
                    break;
                case "journey":
                    NavigatorKind(category.Value, JsonValueKind.Object);
                    NavigatorOptional(category.Value, "phases", phases => NavigatorRecords(phases, row =>
                    {
                        NavigatorStrings(row, "id", "name", "gate", "purpose");
                        NavigatorOptional(row, "contexts", NavigatorStringArray);
                        NavigatorOptional(row, "files", files => NavigatorArray(files, NavigatorFileValue));
                    }));
                    NavigatorOptional(category.Value, "entries", entries => NavigatorRecords(entries, row =>
                    {
                        NavigatorStrings(row, "name", "text");
                        NavigatorFile(row, "file");
                    }));
                    break;
                case "contexts":
                    NavigatorRecords(category.Value, row => NavigatorStrings(row, "id", "name", "cls", "color"));
                    break;
                case "phases":
                    NavigatorRecords(category.Value, row => NavigatorStrings(row, "id", "name", "gate"));
                    break;
                case "procFile":
                    NavigatorMap(category.Value, NavigatorFileValue);
                    break;
                case "dmn":
                    NavigatorMap(category.Value, entry => NavigatorKind(entry, JsonValueKind.String));
                    break;
                // generated is opaque provenance in the producer, not a collection or a timestamp contract.
            }
        }
    }

    private static void ValidateNavigatorMetadata(JsonElement row, bool element)
    {
        NavigatorKind(row, JsonValueKind.Object);
        if (!element)
        {
            NavigatorStrings(row, "phase", "ctx", "ent", "ev", "dmn", "sf", "inc");
            return;
        }

        NavigatorStrings(row, "t", "n");
        // XML attributes/text may be absent in partial models; the Python generator can emit null.
        foreach (var name in NavigatorNullableElementFields)
            NavigatorOptional(row, name, field =>
            {
                if (field.ValueKind != JsonValueKind.Null) NavigatorKind(field, JsonValueKind.String);
            });
        NavigatorFile(row, "tgt");
        foreach (var name in NavigatorElementAssociations)
            NavigatorOptional(row, name, items => NavigatorArray(items, item =>
            {
                if (item.ValueKind != JsonValueKind.Null) NavigatorKind(item, JsonValueKind.String);
            }));
        NavigatorOptional(row, "ev", events => NavigatorTuples(events, 3, fileReference: false));
    }

    private static void NavigatorKind(JsonElement value, JsonValueKind kind)
    {
        if (value.ValueKind != kind) Reject("invalid", "invalid-navigator-category-shape");
    }

    private static void NavigatorOptional(JsonElement row, string name, Action<JsonElement> validate)
    {
        if (row.TryGetProperty(name, out var value)) validate(value);
    }

    private static void NavigatorStrings(JsonElement row, params string[] names)
    {
        foreach (var name in names)
            NavigatorOptional(row, name, value => NavigatorKind(value, JsonValueKind.String));
    }

    private static void NavigatorFile(JsonElement row, string name) =>
        NavigatorOptional(row, name, NavigatorFileValue);

    private static void NavigatorFileValue(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String || !IsSafeRelativePath(value.GetString() ?? ""))
            Reject("invalid", "invalid-navigator-file-reference");
    }

    private static void NavigatorArray(JsonElement value, Action<JsonElement> validate)
    {
        NavigatorKind(value, JsonValueKind.Array);
        foreach (var item in value.EnumerateArray()) validate(item);
    }

    private static void NavigatorStringArray(JsonElement value) =>
        NavigatorArray(value, item => NavigatorKind(item, JsonValueKind.String));

    private static void NavigatorRecords(JsonElement value, Action<JsonElement> validate) =>
        NavigatorArray(value, row =>
        {
            NavigatorKind(row, JsonValueKind.Object);
            validate(row);
        });

    private static void NavigatorMap(JsonElement value, Action<JsonElement> validate, bool fileKeys = false)
    {
        NavigatorKind(value, JsonValueKind.Object);
        foreach (var entry in value.EnumerateObject())
        {
            if (fileKeys && !IsSafeRelativePath(entry.Name)) Reject("invalid", "invalid-navigator-file-reference");
            validate(entry.Value);
        }
    }

    private static void NavigatorTuples(JsonElement value, int length, bool fileReference)
    {
        NavigatorArray(value, tuple =>
        {
            NavigatorKind(tuple, JsonValueKind.Array);
            if (tuple.GetArrayLength() != length) Reject("invalid", "invalid-navigator-category-shape");
            NavigatorStringArray(tuple);
            if (fileReference) NavigatorFileValue(tuple[0]);
        });
    }
}
