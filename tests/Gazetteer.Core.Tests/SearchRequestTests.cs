using Gazetteer.Core.DTOs;
using Gazetteer.Core.Enums;
using Xunit;

namespace Gazetteer.Core.Tests;

public class SearchRequestTests
{
    [Fact]
    public void SearchRequest_DefaultLimit_Is20()
    {
        var request = new SearchRequest();
        Assert.Equal(20, request.Limit);
    }

    [Fact]
    public void SearchRequest_CanSetAllProperties()
    {
        var request = new SearchRequest
        {
            Query = "London",
            CountryCode = "GB",
            LocationType = LocationType.City,
            Limit = 10
        };

        Assert.Equal("London", request.Query);
        Assert.Equal("GB", request.CountryCode);
        Assert.Equal(LocationType.City, request.LocationType);
        Assert.Equal(10, request.Limit);
    }

    [Fact]
    public void SearchResultDto_ParentChain_DefaultsToEmpty()
    {
        var result = new SearchResultDto();
        Assert.NotNull(result.ParentChain);
        Assert.Empty(result.ParentChain);
    }
}
