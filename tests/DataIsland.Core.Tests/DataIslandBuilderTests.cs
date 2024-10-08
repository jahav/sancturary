﻿using System.Diagnostics;
using System.Linq.Expressions;
using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace DataIsland.Core.Tests;

public class DataIslandBuilderTests
{
    #region AddPatcher

    [Fact]
    public void Each_data_access_type_can_have_only_one_patcher()
    {
        var patcher1 = new Mock<IDependencyPatcher<TestDataAccess>>();
        var patcher2 = new Mock<IDependencyPatcher<TestDataAccess>>();
        var sut = new DataIslandBuilder()
            .AddPatcher(patcher1.Object);

        var ex = Assert.Throws<ArgumentException>(() => sut.AddPatcher(patcher2.Object));
        Assert.StartsWith("An item with the same key has already been added.", ex.Message);
    }

    [Fact]
    public void Custom_patcher_patches_services_and_uses_resolved_tenant()
    {
        // Arrange
        var component = new DummyComponent();
        var tenant = new DummyTenant();
        var patchedDataAccess = new TestDataAccess();
        var poolMock = CreateComponentPoolMock<DummyComponent, DummyComponentSpec>("component", component);
        var factoryMock = CreateTenantFactory<DummyComponent, DummyTenant, DummyTenantSpec>(component, tenant);
        var patcherCalled = false;
        var island = new DataIslandBuilder()
            .AddTemplate("template", template =>
            {
                template.AddComponent<DummyComponent, DummyComponentSpec>("component");
                template.AddTenant<DummyTenant, DummyTenantSpec>("tenant", "component");
                template.AddDataAccess<TestDataAccess>("tenant");
            })
            .AddComponentPool(poolMock.Object, factoryMock.Object)
            .AddPatcher<TestDataAccess, DummyTenant>((_, patcherTenant) =>
            {
                patcherCalled = true;
                Assert.Same(tenant, patcherTenant);
                return patchedDataAccess;
            })
            .Build();
        var services = new ServiceCollection();
        services.AddScoped<TestDataAccess>();
        var testContext = new Mock<ITestContext>();
        testContext.Setup(x => x.GetTenant<DummyTenant>(typeof(TestDataAccess))).Returns(tenant);
        services.AddSingleton(testContext.Object);

        // Act
        island.PatchServices(services);
        var serviceProvider = services.BuildServiceProvider();
        var resolvedDataAccess = serviceProvider.GetRequiredService<TestDataAccess>();

        // Assert
        Assert.True(patcherCalled);
        Assert.Same(patchedDataAccess, resolvedDataAccess);
    }

    [Fact]
    public void Custom_patcher_requires_existing_service()
    {
        // Arrange
        var island = StartBuilder<DummyComponent, DummyComponentSpec, DummyTenant, DummyTenantSpec>()
            .AddTemplate("template", template =>
            {
                template.AddComponent<DummyComponent, DummyComponentSpec>("component");
                template.AddTenant<DummyTenant, DummyTenantSpec>("tenant", "component");
                template.AddDataAccess<TestDataAccess>("tenant");
            })
            .AddPatcher<TestDataAccess, DummyTenant>((_, _) => throw new UnreachableException())
            .Build();
        var services = new ServiceCollection();

        // Act
        var ex = Assert.Throws<InvalidOperationException>(() => island.PatchServices(services));

        // Assert
        Assert.Equal("Service DataIsland.Core.Tests.DataIslandBuilderTests+TestDataAccess isn't in the service collection - it can't be patched. Ensure the service is registered as a service before calling the patch method to patch the registration.", ex.Message);
    }

    #endregion

    #region AddComponent

    [Fact]
    public void Component_has_specification_passed_to_pool()
    {
        var retrievedNumber = -1;
        var pool = new Mock<IComponentPool<DummyComponent, DummyComponentSpec>>();
        pool
            .Setup(p => p.AcquireComponentsAsync(It.IsAny<IReadOnlyDictionary<string, DummyComponentSpec>>()))
            .ReturnsAsync((IReadOnlyDictionary<string, DummyComponentSpec> p) =>
            {
                // Specification from builder is passed to the AcquireComponentsAsync method
                // Can't use assert here, because exception is eaten by Moq.
                retrievedNumber = p.Single().Value.Number;
                return new Dictionary<string, DummyComponent> { { "tenant", new DummyComponent() } };
            });
        var factory = new Mock<ITenantFactory<DummyComponent, DummyTenant, DummyTenantSpec>>();
        var dataIsland = new DataIslandBuilder()
            .AddComponentPool(pool.Object, factory.Object)
            .AddTemplate("template", template =>
            {
                template.AddDataAccess<TestDataAccess>("tenant");
                template.AddTenant<DummyTenant, DummyTenantSpec>("tenant", "component");
                template.AddComponent<DummyComponent, DummyComponentSpec>("component", spec => spec.WithNumber(5));
            })
            .AddPatcher(Mock.Of<IDependencyPatcher<TestDataAccess>>())
            .Build();

        dataIsland.Materializer.MaterializeTenantsAsync("template");

        Assert.Equal(5, retrievedNumber);
    }

    [Fact]
    public void Each_component_type_has_exactly_one_pool()
    {
        var builder = new DataIslandBuilder();

        builder.AddComponentPool(
            Mock.Of<IComponentPool<DummyComponent, DummyComponentSpec>>(),
            Mock.Of<ITenantFactory<DummyComponent, DummyTenant, DummyTenantSpec>>());

        var ex = Assert.Throws<ArgumentException>(() =>
        {
            // Try to register same component with a different name.
            builder.AddComponentPool(
                Mock.Of<IComponentPool<DummyComponent, DummyComponentSpec>>(),
                Mock.Of<ITenantFactory<DummyComponent, DummyTenant, DummyTenantSpec>>());
        });
        Assert.Equal("Component pool for DummyComponent is already registered.", ex.Message);
    }

    #endregion

    #region AddTenant

    [Fact]
    public async Task Tenant_has_specification_passed_to_factory()
    {
        var pool = new Mock<IComponentPool<DummyComponent, DummyComponentSpec>>();
        pool
            .Setup(p => p.AcquireComponentsAsync(It.IsAny<IReadOnlyDictionary<string, DummyComponentSpec>>()))
            .ReturnsAsync(new Dictionary<string, DummyComponent> { { "component", new DummyComponent() } });

        var factory = new Mock<ITenantFactory<DummyComponent, DummyTenant, DummyTenantSpec>>();
        factory
            .Setup(f => f.AddTenantAsync(It.IsAny<DummyComponent>(), It.Is<DummyTenantSpec>(s => s.Text == "Hello")))
            .ReturnsAsync(new DummyTenant());

        var dataIsland = new DataIslandBuilder()
            .AddComponentPool(pool.Object, factory.Object)
            .AddTemplate("template", template =>
            {
                template.AddDataAccess<TestDataAccess>("tenant");
                template.AddTenant<DummyTenant, DummyTenantSpec>("tenant", "component", spec => spec.WithText("Hello"));
                template.AddComponent<DummyComponent, DummyComponentSpec>("component");
            })
            .AddPatcher(Mock.Of<IDependencyPatcher<TestDataAccess>>())
            .Build();

        await dataIsland.Materializer.MaterializeTenantsAsync("template");

        factory.Verify(f => f.AddTenantAsync(It.IsAny<DummyComponent>(), It.Is<DummyTenantSpec>(s => s.Text == "Hello")), Times.Once);
    }

    #endregion

    #region AddAfterInit

    [Fact]
    public async Task AfterInit_hook_is_called_after_materialization()
    {
        var component = new DummyComponent();
        var factoryTenant = new DummyTenant();
        var pool = CreateComponentPoolMock<DummyComponent, DummyComponentSpec>("component", component);
        var factory = CreateTenantFactory<DummyComponent, DummyTenant, DummyTenantSpec>(component, factoryTenant);
        var afterInitCalled = false;

        var dataIsland = new DataIslandBuilder()
            .AddComponentPool(pool.Object, factory.Object)
            .AddPatcher(Mock.Of<IDependencyPatcher<TestDataAccess>>())
            .AddTemplate("template", template =>
            {
                template.AddComponent<DummyComponent, DummyComponentSpec>("component");
                template.AddTenant<DummyTenant, DummyTenantSpec>("tenant", "component");
                template.AddDataAccess<TestDataAccess>("tenant");
                template.AddAfterInit((tenants, _) =>
                {
                    afterInitCalled = true;
                    var afterInitTenant = tenants.Single(x => x.TenantName == "tenant").Instance;
                    Assert.Same(factoryTenant, afterInitTenant);
                    return Task.CompletedTask;
                });
            })
            .Build();

        Assert.False(afterInitCalled, "Shouldn't be called before materialization.");

        await dataIsland.Materializer.MaterializeTenantsAsync("template");

        Assert.True(afterInitCalled);
    }

    #endregion

    #region Build

    #region DataAccess

    [Fact]
    public void Template_must_have_at_least_one_data_access()
    {
        var builder = new DataIslandBuilder()
            .AddTemplate("template name", _ => { });

        var ex = Assert.Throws<InvalidOperationException>(() => builder.Build());
        Assert.Equal("Template 'template name' doesn't specify any data access. Add it by using tenant.AddDataAccess method.", ex.Message);

    }

    #endregion

    #region Tenants

    [Fact]
    public void Template_must_specify_all_used_tenants()
    {
        var builder = new DataIslandBuilder()
            .AddTemplate("template name", template =>
            {
                template.AddDataAccess<TestDataAccess>("unspecified tenant");
            });

        var ex = Assert.Throws<InvalidOperationException>(() => builder.Build());
        Assert.Equal("Template 'template name' didn't specify component 'unspecified tenant'. Specify the tenant in the template by template.AddTenant(tenantName). Method has optional second parameter that contains required properties of tenant that will be created.", ex.Message);
    }

    [Fact]
    public void All_specified_tenants_must_be_used()
    {
        var builder = new DataIslandBuilder()
            .AddComponentPool(
                Mock.Of<IComponentPool<DummyComponent, ComponentSpec<DummyComponent>>>(),
                Mock.Of<ITenantFactory<DummyComponent, DummyTenant, TenantSpec<DummyTenant>>>())
            .AddTemplate("template name", template =>
            {
                // No data access
                template.AddTenant<DummyTenant, DummyTenantSpec>("unused tenant", "component");
                template.AddComponent<DummyComponent, DummyComponentSpec>("component");
            });

        var ex = Assert.Throws<InvalidOperationException>(() => builder.Build());
        Assert.Equal("Template 'template name' specifies unused tenants 'unused tenant'. Remove them.", ex.Message);
    }

    #endregion

    #region Components

    [Fact]
    public void Component_must_have_component_pool()
    {
        var builder = new DataIslandBuilder()
            .AddComponentPool(
                Mock.Of<IComponentPool<DummyComponent2, ComponentSpec<DummyComponent2>>>(),
                Mock.Of<ITenantFactory<DummyComponent2, DummyTenant, TenantSpec<DummyTenant>>>())
            .AddTemplate("template name", template =>
            {
                template.AddDataAccess<TestDataAccess>("tenant");
                template.AddTenant<DummyTenant, DummyTenantSpec>("tenant", "component with missing pool");
                template.AddComponent<DummyComponent, DummyComponentSpec>("component with missing pool");
            });

        var ex = Assert.Throws<InvalidOperationException>(() => builder.Build());
        Assert.Equal("Unable to find pool for component of type DummyComponent ('component with missing pool'). Components with pools: 'DummyComponent2'. Use method DataIslandBuilder.AddComponentPool() to add a pool.", ex.Message);
    }

    [Fact]
    public void Template_must_specify_all_used_components()
    {
        // Each template must explicitly specify used components. template.AddComponents is
        // for specifying component specs, i.e. which component from pool to take. It's
        // not there just to declare I need a component from this pool.
        var builder = new DataIslandBuilder()
            .AddComponentPool(
                Mock.Of<IComponentPool<DummyComponent, ComponentSpec<DummyComponent>>>(),
                Mock.Of<ITenantFactory<DummyComponent, DummyTenant, TenantSpec<DummyTenant>>>())
            .AddTemplate("template name", template =>
            {
                template.AddTenant<DummyTenant, DummyTenantSpec>("tenant", "component name");
            });

        var ex = Assert.Throws<InvalidOperationException>(() => builder.Build());
        Assert.Equal("Template 'template name' didn't specify component 'component name'. Specify the component in the template by template.AddComponent(\"component name\") for the template. Method has optional second parameter that contains required properties of component resolved from the pool.", ex.Message);
    }

    [Fact]
    public void All_specified_components_must_be_used()
    {
        var builder = new DataIslandBuilder()
            .AddComponentPool(
                Mock.Of<IComponentPool<DummyComponent, ComponentSpec<DummyComponent>>>(),
                Mock.Of<ITenantFactory<DummyComponent, DummyTenant, TenantSpec<DummyTenant>>>())
            .AddTemplate("template name", template =>
            {
                template.AddComponent<DummyComponent, DummyComponentSpec>("unused component");
            });

        var ex = Assert.Throws<InvalidOperationException>(() => builder.Build());
        Assert.Equal("Template 'template name' specified component 'unused component', but that component wasn't used. Remove it.", ex.Message);
    }

    #endregion

    #region Patchers

    [Fact]
    public void All_data_access_types_must_have_patcher()
    {
        var builder = StartBuilder<DummyComponent, DummyComponentSpec, DummyTenant, DummyTenantSpec>();
        builder = builder
            .AddTemplate("template name", template =>
            {
                template.AddDataAccess<TestDataAccess>("tenant");
                template.AddDataAccess<TestDataAccess2>("tenant");
                template.AddTenant<DummyTenant, DummyTenantSpec>("tenant", "component");
                template.AddComponent<DummyComponent, DummyComponentSpec>("component");
            })
            .AddPatcher(Mock.Of<IDependencyPatcher<TestDataAccess>>());

        var ex = Assert.Throws<InvalidOperationException>(() => builder.Build());
        Assert.Equal("Data access TestDataAccess2 is used by a template 'template name', but doesn't have a patcher. Define patcher with a DataIslandBuilder.AddPatcher() method.", ex.Message);
    }

    #endregion

    #endregion

    private static DataIslandBuilder StartBuilder<TComponent, TComponentSpec, TTenant, TTenantSpec>()
        where TComponent : new()
        where TComponentSpec : ComponentSpec<TComponent>
        where TTenant : new()
        where TTenantSpec : TenantSpec<TTenant>
    {
        var poolMock = new Mock<IComponentPool<TComponent, TComponentSpec>>();
        poolMock
            .Setup(p => p.AcquireComponentsAsync(It.IsAny<IReadOnlyDictionary<string, TComponentSpec>>()))
            .ReturnsAsync((IReadOnlyDictionary<string, TComponentSpec> a) => a.ToDictionary(b => b.Key, _ => new TComponent()));

        var factoryMock = new Mock<ITenantFactory<TComponent, TTenant, TTenantSpec>>();
        factoryMock
            .Setup(f => f.AddTenantAsync(It.IsAny<TComponent>(), It.IsAny<TTenantSpec>()))
            .ReturnsAsync((TComponent _, TTenantSpec _) => new TTenant());

        return new DataIslandBuilder()
            .AddComponentPool(poolMock.Object, factoryMock.Object);
    }

    private static Mock<IComponentPool<TComponent, TComponentSpec>> CreateComponentPoolMock<TComponent, TComponentSpec>(string name, TComponent component)
        where TComponentSpec : ComponentSpec<TComponent>
    {
        var poolMock = new Mock<IComponentPool<TComponent, TComponentSpec>>();
        poolMock
            .Setup(p => p.AcquireComponentsAsync(It.Is<IReadOnlyDictionary<string, TComponentSpec>>(d => d.Count == 1 && d.ContainsKey(name))))
            .ReturnsAsync(new Dictionary<string, TComponent> { { name, component } });

        return poolMock;
    }

    private static Mock<ITenantFactory<TComponent, TTenant, TTenantSpec>> CreateTenantFactory<TComponent, TTenant, TTenantSpec>(
        TComponent component, TTenant tenant, Expression<Func<TTenantSpec, bool>>? match = null)
        where TTenantSpec : TenantSpec<TTenant>
    {
        match ??= _ => true;
        var factory = new Mock<ITenantFactory<TComponent, TTenant, TTenantSpec>>();
        factory
            .Setup(f => f.AddTenantAsync(It.Is<TComponent>(c => ReferenceEquals(c, component)), It.Is(match)))
            .ReturnsAsync(tenant);

        return factory;
    }

    [UsedImplicitly]
    public class TestDataAccess;

    [UsedImplicitly]
    public class TestDataAccess2;

    [UsedImplicitly]
    public class DummyTenant;

    [UsedImplicitly]
    public class DummyComponent;

    [UsedImplicitly]
    public class DummyComponent2;

    [UsedImplicitly]
    public record DummyTenantSpec : TenantSpec<DummyTenant>
    {
        public string? Text { get; private init; }

        public DummyTenantSpec WithText(string text)
        {
            return this with { Text = text };
        }
    }

    [UsedImplicitly]
    public record DummyComponentSpec : ComponentSpec<DummyComponent>
    {
        public int Number { get; private init; }

#pragma warning disable CA1822
        public DummyComponentSpec WithNumber(int number)
        {
            return new DummyComponentSpec { Number = number };
        }
#pragma warning restore CA1822
    }
}
