using SupportChat.Api.Contracts;

namespace SupportChat.Api.Tests;

public class MapperTests
{
    [Fact]
    public void No_response_DTO_declares_an_internal_field()
    {
        var dtoTypes = typeof(SessionDto).Assembly.GetTypes()
            .Where(t => t.Namespace == "SupportChat.Api.Contracts" && t.Name.EndsWith("Dto", StringComparison.Ordinal));
        foreach (var type in dtoTypes)
        {
            var names = type.GetProperties().Select(p => p.Name);
            Assert.Empty(names.Intersect(Helpers.InternalFields, StringComparer.OrdinalIgnoreCase));
        }
    }
}
