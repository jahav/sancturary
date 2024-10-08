﻿using Microsoft.Extensions.DependencyInjection;
using DataIsland.xUnit.v3;
using Xunit;

namespace DataIsland.Demo;

[ApplyTemplate("DefaultTemplate")]
public class UnitTest3 : IClassFixture<ClassFixture>
{
    private readonly ClassFixture _fixture;

    public UnitTest3(ClassFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Test2()
    {
        using (var scope = _fixture.ServiceProvider.CreateScope())
        {
            var test = scope.ServiceProvider.GetRequiredService<QueryDbContext>();
            var list = test.Users.Select(x => x.Name).OrderBy(x => x).ToList();
            Assert.Equal(new[] { "Dummy A", "Dummy B", "Dummy C", }, list);

            await test.Users.AddAsync(new Users
            {
                UserId = Guid.NewGuid(),
                Name = "Test2",
            });
            await test.SaveChangesAsync();

            await Task.Delay(10000);

            var list1 = test.Users.Select(x => x.Name).OrderBy(x => x).ToList();
            Assert.Equal(new[] { "Dummy A", "Dummy B", "Dummy C", "Test2" }, list1);
        }
    }
}
