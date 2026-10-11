using RemoteDock.Core;

namespace RemoteDock.Core.Tests;

public sealed class ConnectionGroupTests
{
    [Fact]
    public void NestedDefaultsApplyParentThenChildWhileLocalOverridesWin()
    {
        var parent = new ConnectionGroup { Defaults = new() { UserName = "operator", Domain = "EXAMPLE", Scale = new(1, 2), Port = 3390 } };
        var child = new ConnectionGroup { ParentId = parent.Id, Defaults = new() { Scale = new(2, 3), Domain = "" } };
        var profile = new ConnectionProfile { Host = "server", GroupId = child.Id, Inherit = InheritedSettings.SignIn | InheritedSettings.Display, Port = 3391 };
        var catalog = new ConnectionCatalog { Groups = [parent, child], Connections = [profile] };
        catalog.Validate();
        var effective = catalog.Resolve(profile);
        Assert.Equal("operator", effective.UserName);
        Assert.Equal("", effective.Domain);
        Assert.Equal(new ResolutionScale(2, 3), effective.EffectiveScale);
        Assert.Equal(3391, effective.Port);
    }

    [Fact]
    public void GroupChangesFlowToChildrenWithoutOverwritingTheirSavedValues()
    {
        var group = new ConnectionGroup { Defaults = new() { Scale = new(1, 2) } };
        var profile = new ConnectionProfile { Host = "server", GroupId = group.Id, Inherit = InheritedSettings.Display, Scale = new(1, 1) };
        var catalog = new ConnectionCatalog { Groups = [group], Connections = [profile] };
        var updated = catalog with { Groups = [group with { Defaults = new() { Scale = new(2, 3) } }] };
        Assert.Equal(new ResolutionScale(2, 3), updated.Resolve(profile).EffectiveScale);
        Assert.Equal(new ResolutionScale(1, 1), profile.EffectiveScale);
    }

    [Fact]
    public void ExplicitFalseGroupValuesOverrideTrueDefaults()
    {
        var group = new ConnectionGroup { Defaults = new() { RedirectClipboard = false } };
        var profile = new ConnectionProfile { Host = "server", GroupId = group.Id, Inherit = InheritedSettings.Redirection };
        Assert.False(new ConnectionCatalog { Groups = [group] }.Resolve(profile).RedirectClipboard);
    }

    [Fact]
    public void MissingGroupsAndParentCyclesAreRejected()
    {
        var a = new ConnectionGroup();
        var b = new ConnectionGroup { ParentId = a.Id };
        Assert.Throws<InvalidDataException>(() => new ConnectionCatalog { Groups = [a with { ParentId = b.Id }, b] }.Validate());
        Assert.Throws<InvalidDataException>(() => new ConnectionCatalog { Connections = [new() { Host = "server", GroupId = Guid.NewGuid() }] }.Validate());
    }

    [Fact]
    public void InvalidSharedParametersAreRejectedBeforeSaving()
    {
        Assert.Throws<ArgumentException>(() => new ConnectionCatalog { Groups = [new() { Defaults = new() { Port = 70000 } }] }.Validate());
    }
}
