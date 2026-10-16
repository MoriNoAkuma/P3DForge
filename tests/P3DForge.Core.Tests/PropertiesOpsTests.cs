using P3DForge.Core.IO;
using P3DForge.Core.Model;
using P3DForge.Core.Ops;

namespace P3DForge.Core.Tests;

public class PropertiesOpsTests
{
    static Lod Sample() => SelectionsOpsTests.TwoQuads(); // tags: #Property# autocenter, left, Right, #Selected#

    static string[] Keys(Lod lod) => lod.Properties.Select(p => p.Key).ToArray();

    [Fact]
    public void Set_UpdatesInPlace_KeepsKeySpelling_ClearsRaw()
    {
        var lod = Sample();
        lod = lod with { Tags = lod.Tags.SetItem(0, new PropertyTag(1, "AutoCenter", "0", new byte[128])) };
        var next = NamedProperties.Set(lod, "autocenter", "1");
        var p = Assert.IsType<PropertyTag>(next.Tags[0]);
        Assert.Equal("AutoCenter", p.Key);
        Assert.Equal("1", p.Value);
        Assert.Null(p.Raw);
        Assert.Equal("0", ((PropertyTag)lod.Tags[0]).Value);
        Assert.Same(lod, NamedProperties.Set(lod, "AUTOCENTER", "0"));
    }

    [Fact]
    public void Set_NewKey_GoesAfterLastProperty()
    {
        var lod = NamedProperties.Set(Sample(), "lodnoshadow", "1");
        Assert.Equal(["autocenter", "lodnoshadow"], Keys(lod));
        Assert.IsType<PropertyTag>(lod.Tags[1]);
        Assert.IsType<SelectionTag>(lod.Tags[2]);
    }

    [Fact]
    public void Set_WithoutProperties_GoesBeforeFirstSelection()
    {
        var lod = Sample();
        lod = lod with { Tags = lod.Tags.RemoveAt(0).Insert(0, new UVSetTag(1, 0, new float[16])) };
        var next = NamedProperties.Set(lod, "class", "house");
        Assert.IsType<UVSetTag>(next.Tags[0]);
        Assert.IsType<PropertyTag>(next.Tags[1]);
        Assert.IsType<SelectionTag>(next.Tags[2]);

        var bare = lod with { Tags = [] };
        Assert.Single(NamedProperties.Set(bare, "a", "b").Tags);
    }

    [Fact]
    public void Set_Validates()
    {
        var lod = Sample();
        Assert.Throws<ArgumentException>(() => NamedProperties.Set(lod, "", "x"));
        Assert.Throws<ArgumentException>(() => NamedProperties.Set(lod, " ", "x"));
        Assert.Throws<ArgumentException>(() => NamedProperties.Set(lod, new string('k', 64), "x"));
        Assert.Throws<ArgumentException>(() => NamedProperties.Set(lod, "k", new string('v', 64)));
        Assert.Throws<ArgumentException>(() => NamedProperties.Set(lod, "k", "колесо"));
        Assert.Equal(63, NamedProperties.Set(lod, new string('k', 63), "").Properties.Last().Key.Length);
        Assert.Equal("", NamedProperties.Set(lod, "empty", "").Property("empty"));
    }

    [Fact]
    public void Rename_KeepsPositionAndValue()
    {
        var lod = NamedProperties.Set(Sample(), "lodnoshadow", "1");
        var next = NamedProperties.Rename(lod, "autocenter", "canocclude");
        Assert.Equal(["canocclude", "lodnoshadow"], Keys(next));
        Assert.Equal("0", next.Property("canocclude"));
        Assert.Equal(["AUTOCENTER", "lodnoshadow"], Keys(NamedProperties.Rename(lod, "autocenter", "AUTOCENTER")));
        Assert.Same(lod, NamedProperties.Rename(lod, "autocenter", "autocenter"));
        Assert.Throws<ArgumentException>(() => NamedProperties.Rename(lod, "autocenter", "LODNOSHADOW"));
        Assert.Throws<ArgumentException>(() => NamedProperties.Rename(lod, "missing", "x"));
        Assert.Throws<ArgumentException>(() => NamedProperties.Rename(lod, 2, "x")); // tag 2 is a selection
    }

    [Fact]
    public void Delete_ByKeyRemovesAll_ByIndexRemovesOne()
    {
        var lod = Sample();
        lod = lod with { Tags = lod.Tags.Insert(1, new PropertyTag(1, "AUTOCENTER", "1", null)) };
        Assert.Empty(NamedProperties.Delete(lod, "autocenter").Properties);
        var one = NamedProperties.DeleteAt(lod, 1);
        Assert.Equal(["autocenter"], Keys(one));
        Assert.Same(lod, NamedProperties.Delete(lod, "missing"));
        Assert.Same(lod, NamedProperties.DeleteAt(lod, 2));
    }

    [Fact]
    public void SetValue_ByIndex()
    {
        var lod = Sample();
        var next = NamedProperties.SetValue(lod, 0, "1");
        Assert.Equal("1", next.Property("autocenter"));
        Assert.Throws<ArgumentException>(() => NamedProperties.SetValue(lod, 1, "1"));
    }

    [Fact]
    public void EditedProperty_RoundTripsThroughMlod()
    {
        var lod = NamedProperties.Set(Sample(), "class", "house");
        lod = NamedProperties.Set(lod, "autocenter", "1");
        var back = Mlod.Read(Mlod.Write(new P3DModel([lod]))).Lods[0];
        Assert.Equal("house", back.Property("class"));
        Assert.Equal("1", back.Property("autocenter"));
        Assert.Equal(["autocenter", "class"], Keys(back));
    }

    // ---- hints ---------------------------------------------------------------------------------------

    [Fact]
    public void Hints_LookupAndValueProblems()
    {
        Assert.NotNull(PropertyHints.Get(" AutoCenter "));
        Assert.Equal("dammage", PropertyHints.Get("dammage")!.Key);
        Assert.Null(PropertyHints.Get("nonsense"));
        Assert.Null(PropertyHints.ValueProblem("autocenter", "0"));
        Assert.NotNull(PropertyHints.ValueProblem("autocenter", "yes"));
        Assert.Null(PropertyHints.ValueProblem("class", "House"));
        Assert.NotNull(PropertyHints.ValueProblem("class", "castle"));
        Assert.Null(PropertyHints.ValueProblem("drawimportance", "0.25"));
        Assert.NotNull(PropertyHints.ValueProblem("drawimportance", "abc"));
        Assert.Null(PropertyHints.ValueProblem("shadowlod", "-1"));
        Assert.NotNull(PropertyHints.ValueProblem("shadowlod", "1.5"));
        Assert.Null(PropertyHints.ValueProblem("nonsense", "whatever"));
        Assert.Contains("Values: 0, 1", PropertyHints.Text("autocenter"));
    }

    [Fact]
    public void Hints_SuggestedKeys_DayZFirst_ExcludesPresent()
    {
        var keys = PropertyHints.SuggestedKeys(Sample());
        Assert.DoesNotContain("autocenter", keys);
        int lastDayZ = keys.ToList().FindLastIndex(k => PropertyHints.Get(k)!.DayZ);
        int firstOther = keys.ToList().FindIndex(k => !PropertyHints.Get(k)!.DayZ);
        Assert.True(lastDayZ < firstOther);
        Assert.Equal(PropertyHints.Known.Count - 1, keys.Count);
    }

    [Fact]
    public void Hints_Warnings()
    {
        var lod = Sample();
        lod = lod with { Tags = lod.Tags.Insert(1, new PropertyTag(1, "Autocenter", "2", null)).Insert(2, new PropertyTag(1, "foo ", " x", null)) };
        var w0 = PropertyHints.Warnings(lod, 0);
        Assert.Contains(w0, w => w.StartsWith("Duplicate key (2"));
        var w1 = PropertyHints.Warnings(lod, 1);
        Assert.Contains(w1, w => w.Contains("ignored"));
        Assert.Contains("Expected 0 or 1", w1);
        var w2 = PropertyHints.Warnings(lod, 2);
        Assert.Contains(w2, w => w.StartsWith("Unknown property"));
        Assert.Contains(w2, w => w.Contains("name has leading"));
        Assert.Contains(w2, w => w.Contains("value has leading"));
        Assert.Empty(PropertyHints.Warnings(lod, 3)); // a selection tag
    }

    [Fact]
    public void Hints_Typical()
    {
        Assert.Contains(("autocenter", "0"), PropertyHints.Typical(1e13f));
        Assert.Contains(("lodnoshadow", "1"), PropertyHints.Typical(1f));
        Assert.Contains(("sbsource", "shadowvolume"), PropertyHints.Typical(6e15f));
    }
}
