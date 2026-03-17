using Gazetteer.Core.Enums;
using Gazetteer.Core.Models;
using Xunit;

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
        Assert.Null(location.ParentId);
        Assert.Null(location.Geometry);
        Assert.Null(location.Population);
        Assert.Empty(location.Children);
    }

    [Fact]
    public void Location_CanSetProperties()
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
            Latitude = 51.5074,
            Longitude = -0.1278,
            Population = 8_982_000
        };

        Assert.Equal("London", location.Name);
        Assert.Equal(LocationType.City, location.LocationType);
        Assert.Equal("GB", location.CountryCode);
        Assert.Equal(51.5074, location.Latitude);
    }

    [Fact]
    public void Location_ParentChildRelationship_Works()
    {
        var country = new Location { Id = 1, Name = "United Kingdom", LocationType = LocationType.Country };
        var region = new Location { Id = 2, Name = "England", LocationType = LocationType.AdminRegion1, ParentId = 1, Parent = country };
        var city = new Location { Id = 3, Name = "London", LocationType = LocationType.City, ParentId = 2, Parent = region };

        Assert.Equal("England", city.Parent!.Name);
        Assert.Equal("United Kingdom", city.Parent.Parent!.Name);
    }

    [Fact]
    public void Country_DefaultValues_AreCorrect()
    {
        var country = new Country();

        Assert.Equal(string.Empty, country.Code);
        Assert.Equal(string.Empty, country.Name);
        Assert.Equal(string.Empty, country.Continent);
    }

    [Theory]
    [InlineData(LocationType.Country)]
    [InlineData(LocationType.City)]
    [InlineData(LocationType.Postcode)]
    [InlineData(LocationType.Road)]
    public void LocationType_AllValues_AreDefined(LocationType type)
    {
        Assert.True(Enum.IsDefined(type));
    }
}
