﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Xunit.v3;

namespace Sanctuary.xUnit.v3;

public class ScopedTenantsAttribute(string _profileName) : BeforeAfterTestAttribute
{
    public ScopedTenantsAttribute() : this("DefaultProfile")
    {
    }

    public override async ValueTask Before(MethodInfo methodUnderTest, IXunitTest test)
    {
        var ctx = Xunit.TestContext.Current;

        var tenantsFactory = GetTenantsFactory(ctx, test);
        var tenants = await tenantsFactory.AddTenantsAsync(_profileName);

        // The KeyValueStorage should never contain the test keys at this point,
        // so use Add in order to throw if there is anything.
        ctx.KeyValueStorage.Add(GetTenantsKey(ctx), tenants);

        var dataAccessMap = tenants
            .SelectMany(tenantInfo => tenantInfo.DataAccess.Select(dataAccess => (dataAccess, tenantInfo)))
            .ToDictionary(x => x.dataAccess, x => x.tenantInfo);
        ctx.KeyValueStorage.Add(GetDataAccessMapKey(ctx), dataAccessMap);
    }

    public override async ValueTask After(MethodInfo methodUnderTest, IXunitTest test)
    {
        var ctx = Xunit.TestContext.Current;

        var tenantsKey = GetTenantsKey(ctx);
        if (!ctx.KeyValueStorage.TryGetValue(tenantsKey, out var untypedTenants) || untypedTenants is null)
            throw new InvalidOperationException("Test didn't have tenants.");

        ctx.KeyValueStorage.Remove(tenantsKey);
        ctx.KeyValueStorage.Remove(GetDataAccessMapKey(ctx));
        var tenants = (IEnumerable<TenantInfo>)untypedTenants;
        var tenantsFactory = GetTenantsFactory(ctx, test);
        await tenantsFactory.RemoveTenantsAsync(tenants);
    }

    private static Type GetFixtureType(IXunitTest test)
    {
        // xUnit requires that all classes have only one ctor, otherwise it is an error.
        var ctor = test.TestMethod.TestClass.Class.GetConstructors().Single();
        var ctorParam = ctor.GetParameters().SingleOrDefault();
        if (ctorParam is null)
            throw new InvalidOperationException("No fixture");

        var fixtureType = ctorParam.ParameterType;
        return fixtureType;
    }

    private static ITenantsFactory GetTenantsFactory(Xunit.TestContext context, IXunitTest test)
    {
        var fixtureType = GetFixtureType(test);
        var tenantsFactory = context.KeyValueStorage[$"{fixtureType.Name}-factory"] as ITenantsFactory;
        if (tenantsFactory is null)
            throw new InvalidOperationException("No factory");
        return tenantsFactory;
    }

    private static string GetTenantsKey(Xunit.TestContext context)
    {
        return GetTestKey(context, "-tenants");
    }

    private static string GetDataAccessMapKey(Xunit.TestContext context)
    {
        return GetTestKey(context, "-data-access-map");
    }

    private static string GetTestKey(Xunit.TestContext context, string suffix)
    {
        var testMethod = context.TestMethod;
        if (testMethod is null)
            throw new InvalidOperationException("Test method is null");

        var key = testMethod.UniqueID + suffix;
        return key;
    }
}