using Gazetteer.Core.Enums;
using Gazetteer.Core.Models;

namespace Gazetteer.Core.Tests;

public class LocationModelTests
{
    [Fact]
    public void Location_DefaultValues_AreCorrect()
    {
        var location = new Location();

        Assert.Equal(0, location.Id);
        Assert.Equal(string.Empty, location.Name);
        Assert.Equal(string.Empty, location.CountryCode);
        Assert.Empty(location.AlternateNames);
        Assert.Empty(location.Children);
        Assert.Null(location.Parent);
        Assert.Null(location.ParentId);
        Assert.Null(location.Geometry);
        Assert.Null(location.Population);
        Assert.Null(location.PostalCode);
    }

    [Fact]
    public void Location_PropertiesCanBeSet()
    {
        var location = new Location
        {
            Id = 1,
            OsmId = 123456,
            OsmType = OsmType.Node,
            Name = "London",
            NameEn = "London",
            LocationType = LocationType.City,
            CountryCode = "GB",
            Lat = 51.5074,
            Lon = -0.1278,
            Population = 8982000
        };

        Assert.Equal(1, location.Id);
        Assert.Equal(123456, location.OsmId);
        Assert.Equal(OsmType.Node, location.OsmType);
        Assert.Equal("London", location.Name);
        Assert.Equal(LocationType.City, location.LocationType);
        Assert.Equal("GB", location.CountryCode);
        Assert.Equal(51.5074, location.Lat);
        Assert.Equal(-0.1278, location.Lon);
        Assert.Equal(8982000, location.Population);
    }

    [Fact]
    public void Location_ParentChildRelationship_Works()
    {
        var parent = new Location { Id = 1, Name = "England", LocationType = LocationType.AdminRegion1 };
        var child = new Location { Id = 2, Name = "London", LocationType = LocationType.City, ParentId = 1, Parent = parent };
        parent.Children.Add(child);

        Assert.Equal(parent, child.Parent);
        Assert.Equal(1, child.ParentId);
        Assert.Single(parent.Children);
        Assert.Equal(child, parent.Children[0]);
    }
}
