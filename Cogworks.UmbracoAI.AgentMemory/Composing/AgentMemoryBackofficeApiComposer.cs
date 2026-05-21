using System.Reflection;
using Asp.Versioning;
using Cogworks.UmbracoAI.AgentMemory.Web.Api;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;
using Umbraco.Cms.Api.Common.OpenApi;
using Umbraco.Cms.Api.Management.OpenApi;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;

namespace Cogworks.UmbracoAI.AgentMemory.Composing;

/// <summary>
/// Story 2.2 — Wires the package's Management-API controllers into Umbraco's
/// Swagger doc generation + auth requirement pipeline. Auto-discovered as
/// <see cref="IComposer"/> alongside <see cref="AgentMemoryComposer"/>; runs
/// at the same composition phase. Sibling composer pattern is sanctioned for
/// distinct registration concerns — this composer covers the Web/Api transport
/// surface (Swagger doc + operation filter + operation-id discipline) while
/// <see cref="AgentMemoryComposer"/> covers core service registration.
/// </summary>
/// <remarks>
/// All three registration sites are idempotent under repeated <see cref="Compose"/>
/// calls, mirroring <see cref="AgentMemoryComposer"/>'s TryAddEnumerable pattern:
/// the <see cref="IOperationIdHandler"/> and <see cref="IConfigureOptions{SwaggerGenOptions}"/>
/// registrations use <c>TryAddEnumerable</c> for (service, impl) tuple dedup; the
/// ApplicationPart registration self-guards inside the configure action.
/// </remarks>
public sealed class AgentMemoryBackofficeApiComposer : IComposer
{
    public void Compose(IUmbracoBuilder builder)
    {
        // Register this package's assembly as an MVC ApplicationPart so the
        // Management-API controllers it carries are discoverable to ASP.NET
        // Core's controller infrastructure. Without this, Umbraco's
        // UseBackOfficeEndpoints() only routes the host's entry-assembly
        // controllers; the package's controllers return HTTP 404.
        var packageAssembly = typeof(AgentFeedbackController).Assembly;
        builder.Services.AddControllers().ConfigureApplicationPartManager(manager =>
        {
            if (manager.ApplicationParts.OfType<AssemblyPart>().Any(p => p.Assembly == packageAssembly))
            {
                return;
            }
            var partFactory = ApplicationPartFactory.GetApplicationPartFactory(packageAssembly);
            foreach (var part in partFactory.GetApplicationParts(packageAssembly))
            {
                manager.ApplicationParts.Add(part);
            }
        });

        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IOperationIdHandler, AgentMemoryOperationIdHandler>());

        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IConfigureOptions<SwaggerGenOptions>, AgentMemorySwaggerGenOptionsSetup>());
    }

    private sealed class AgentMemorySwaggerGenOptionsSetup : IConfigureOptions<SwaggerGenOptions>
    {
        public void Configure(SwaggerGenOptions opt)
        {
            opt.SwaggerDoc(Constants.ApiName, new OpenApiInfo
            {
                Title = "Cogworks AI Agent Memory Backoffice API",
                Version = "1.0",
            });
            opt.OperationFilter<AgentMemoryOperationSecurityFilter>();
        }
    }

    private sealed class AgentMemoryOperationSecurityFilter : BackOfficeSecurityRequirementsOperationFilterBase
    {
        protected override string ApiName => Constants.ApiName;
    }

    /// <summary>
    /// Explicit allow-list of intended Management-API controllers. New
    /// controllers MUST be added to <see cref="AllowedControllers"/>; do NOT
    /// replace this with reflection-based discovery — that re-introduces the
    /// duplicate-operation-id footgun if two future actions named identically
    /// appear across controllers.
    /// </summary>
    private sealed class AgentMemoryOperationIdHandler : OperationIdHandler
    {
        private static readonly Type[] AllowedControllers = new[]
        {
            typeof(AgentFeedbackController),
            typeof(AgentRunReadController),
            typeof(AgentFeedbackReadController),
            typeof(MemoryEntriesReadController),
        };

        public AgentMemoryOperationIdHandler(IOptions<ApiVersioningOptions> apiVersioningOptions)
            : base(apiVersioningOptions)
        {
        }

        protected override bool CanHandle(ApiDescription apiDescription, ControllerActionDescriptor controllerActionDescriptor)
            => Array.IndexOf(AllowedControllers, controllerActionDescriptor.ControllerTypeInfo.AsType()) >= 0;

        public override string Handle(ApiDescription apiDescription)
            => $"{apiDescription.ActionDescriptor.RouteValues["action"]}";
    }
}
