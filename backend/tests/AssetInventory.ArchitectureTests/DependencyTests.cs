using System.Reflection;
using AssetInventory.Application.Catalog;
using AssetInventory.Contracts.Catalog;
using AssetInventory.Domain;
using AssetInventory.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace AssetInventory.ArchitectureTests;

public sealed class DependencyTests
{
    [Fact]
    public void DomainDoesNotDependOnInfrastructureOrApi() => Assert.DoesNotContain(typeof(Asset).Assembly.GetReferencedAssemblies(), x =>
        x.Name!.StartsWith("AssetInventory.") || x.Name.StartsWith("Microsoft.EntityFrameworkCore") || x.Name.StartsWith("Microsoft.AspNetCore"));

    [Fact]
    public void ContractsDoNotDependOnImplementation() => Assert.DoesNotContain(typeof(DepartmentResponse).Assembly.GetReferencedAssemblies(), x => x.Name!.StartsWith("AssetInventory."));

    [Fact]
    public void ApplicationDoesNotDependOnInfrastructureOrApi() => Assert.DoesNotContain(typeof(ICatalogService).Assembly.GetReferencedAssemblies(), x =>
        x.Name is "AssetInventory.Infrastructure" or "AssetInventory.Api" || x.Name!.StartsWith("Microsoft.EntityFrameworkCore"));

    [Fact]
    public void InfrastructureDoesNotDependOnApi() => Assert.DoesNotContain(typeof(AppDbContext).Assembly.GetReferencedAssemblies(), x => x.Name == "AssetInventory.Api");

    [Fact]
    public void ApiUsesMinimalApiWithoutMvcControllers() => Assert.DoesNotContain(Assembly.Load("AssetInventory.Api").GetTypes(), x => typeof(ControllerBase).IsAssignableFrom(x));
}
