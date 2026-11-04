using PCL.Living.Features;

internal static class InstanceResourceKindRulesTests {
    internal static IEnumerable<(string Name, Action Run)> Cases() {
        yield return ("resource kind titles preserve the five LF product labels", Titles);
        yield return ("resource kind folders preserve the five LF storage names", Folders);
        yield return ("resource kind mappings retain exact array index errors", InvalidKinds);
    }

    private static void Titles() {
        Equal("存档", InstanceResourceKindRules.Title(0));
        Equal("截图", InstanceResourceKindRules.Title(1));
        Equal("资源包", InstanceResourceKindRules.Title(2));
        Equal("光影包", InstanceResourceKindRules.Title(3));
        Equal("投影/原理图", InstanceResourceKindRules.Title(4));
    }

    private static void Folders() {
        Equal("saves", InstanceResourceKindRules.FolderName(0));
        Equal("screenshots", InstanceResourceKindRules.FolderName(1));
        Equal("resourcepacks", InstanceResourceKindRules.FolderName(2));
        Equal("shaderpacks", InstanceResourceKindRules.FolderName(3));
        Equal("schematics", InstanceResourceKindRules.FolderName(4));
    }

    private static void InvalidKinds() {
        foreach (int kind in new[] { int.MinValue, -1, 5, int.MaxValue }) {
            OutOfRange(() => InstanceResourceKindRules.Title(kind));
            OutOfRange(() => InstanceResourceKindRules.FolderName(kind));
        }
    }

    private static void OutOfRange(Func<string> read) {
        try { _ = read(); }
        catch (Exception error) {
            if (error.GetType() != typeof(IndexOutOfRangeException)) throw new Exception("Expected exact IndexOutOfRangeException.", error);
            return;
        }
        throw new Exception("Expected IndexOutOfRangeException.");
    }

    private static void Equal(string expected, string actual) {
        if (expected != actual) throw new Exception($"Expected [{expected}], got [{actual}].");
    }
}
