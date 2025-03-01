// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Web;
using Microsoft.AspNetCore.SystemWebAdapters.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ObjectPool;
using Microsoft.Extensions.Options;

namespace Microsoft.AspNetCore.SystemWebAdapters;

/// <summary>
/// A policy to create an HttpApplication, associated modules, and add intrinsic events. For details, see the official documentation for
/// how this worked in ASP.NET Framework: https://docs.microsoft.com/en-us/dotnet/api/system.web.httpapplication#remarks
/// </summary>
internal sealed partial class HttpApplicationPooledObjectPolicy : PooledObjectPolicy<HttpApplication>, IDisposable
{
    private static readonly HashSet<string> UnsupportedEvents = new(StringComparer.OrdinalIgnoreCase)
    {
        // Fired before the ASP.NET page framework sends content to a requesting client (browser).
        "Application_PreSendContent",
        
        // Fired when the last instance of an HttpApplication class is destroyed. It's fired only once during an application's lifetime.
        "Application_End",
    };

    private static readonly Dictionary<string, Action<HttpApplication, EventHandler>> KnownEvents = new(StringComparer.OrdinalIgnoreCase)
    {
        // Fired when the first instance of the HttpApplication class is created. It allows you to create objects that are accessible by all HttpApplication instances.
        { "Application_Start", (app, handler) => app.ApplicationStart += handler },

        // Fired when an application initializes or is first called. It's invoked for all HttpApplication object instances.
        { "Application_Init", (app, handler) => app.ApplicationInit += handler },
        
        // Fired just before an application is destroyed. This is the ideal location for cleaning up previously used resources.
        { "Application_Disposed", (app, handler) => app.Disposed += handler },

        // Fired when an unhandled exception is encountered within the application.
        { "Application_Error", (app, handler) => app.Error += handler },

        // Fired when an application request is received. It's the first event fired for a request, which is often a page request (URL) that a user enters.
        { "Application_BeginRequest", (app, handler) => app.BeginRequest += handler },

        // The last event fired for an application request.
        { "Application_EndRequest", (app, handler) => app.EndRequest += handler },

        // Fired before the ASP.NET page framework begins executing an event handler like a page or Web service.
        { "Application_PreRequestHandlerExecute", (app, handler) => app.PreRequestHandlerExecute += handler },

        // Fired when the ASP.NET page framework is finished executing an event handler.
        { "Application_PostRequestHandlerExecute", (app, handler) => app.PostRequestHandlerExecute += handler },

        // Fired before the ASP.NET page framework sends HTTP headers to a requesting client (browser).
        { "Application_PreSendRequestHeaders", (app, handler) => app.PreSendRequestHeaders += handler },


        // Fired when the ASP.NET page framework gets the current state (Session state) related to the current request.
        { "Application_AcquireRequestState", (app, handler) => app.AcquireRequestState += handler },

        // Fired when the ASP.NET page framework completes execution of all event handlers. This results in all state modules to save their current state data.
        { "Application_ReleaseRequestState", (app, handler) => app.ReleaseRequestState += handler },

        // Fired when the ASP.NET page framework completes an authorization request. It allows caching modules to serve the request from the cache, thus bypassing handler execution.
        { "Application_ResolveRequestCache", (app, handler) => app.ResolveRequestCache += handler },

        // Fired when the ASP.NET page framework completes handler execution to allow caching modules to store responses to be used to handle subsequent requests.
        { "Application_UpdateRequestCache", (app, handler) => app.UpdateRequestCache += handler },

        // Fired when the security module has established the current user's identity as valid. At this point, the user's credentials have been validated.
        { "Application_AuthenticateRequest", (app, handler) => app.AuthenticateRequest += handler },

        // Fired when the security module has verified that a user can access resources.
        { "Application_AuthorizeRequest", (app, handler) => app.AuthorizeRequest += handler },

        // Fired when a new user visits the application Web site.
        { "Session_Start", (app, handler) => app.SessionStart += handler },

        // Fired when a user's session times out, ends, or they leave the application Web site.
        { "Session_End", (app, handler) => app.SessionEnd += handler },
    };

    [LoggerMessage(0, LogLevel.Information, "Registered event {ApplicationType}.{EventName}")]
    private partial void LogRegistration(string applicationType, string eventName);

    [LoggerMessage(1, LogLevel.Warning, "HttpApplication event {ApplicationType}.{EventName} is unsupported")]
    private partial void LogUnsupported(string applicationType, string eventName);

    [LoggerMessage(2, LogLevel.Warning, "{ApplicationType}.{EventName} has unsupported signature")]
    private partial void LogInvalid(string applicationType, string eventName);

    private readonly ILogger<HttpApplicationPooledObjectPolicy> _logger;
    private readonly IServiceProvider _services;
    private readonly Lazy<Func<IServiceProvider, HttpApplication>> _factory;
    private readonly HttpApplicationState _state;

    public HttpApplicationPooledObjectPolicy(IServiceProvider services, IOptions<HttpApplicationOptions> options, ILogger<HttpApplicationPooledObjectPolicy> logger)
    {
        _logger = logger;
        _services = services;
        _state = new HttpApplicationState();
        _factory = new Lazy<Func<IServiceProvider, HttpApplication>>(() => CreateFactory(options.Value), isThreadSafe: true);
    }

    public override HttpApplication Create()
    {
        var app = _factory.Value(_services);

        // This is invoked each time an HttpApplication is constructed
        app.InvokeEvent(ApplicationEvent.ApplicationInit);

        return app;
    }

    public override bool Return(HttpApplication obj)
    {
        obj.Context = null!;
        return true;
    }

    /// <summary>
    /// Creates a callback that will regsiter implicit events on <see cref="HttpApplication"/>.
    /// </summary>
    /// <param name="options">Options for the <see cref="HttpApplication"/>.</param>
    /// <returns>A callback to create a new <see cref="HttpApplication"/> instance.</returns>
    /// <seealso cref="https://referencesource.microsoft.com/#System.Web/HttpApplication.cs,b24816e1097719dd"/>
    private Func<IServiceProvider, HttpApplication> CreateFactory(HttpApplicationOptions options)
    {
        var eventInitializer = GetEventInitializer(options);
        var factory = ActivatorUtilities.CreateFactory(options.ApplicationType, Array.Empty<Type>());
        var moduleFactories = options.Modules
            .Select(m => (m.Key, ActivatorUtilities.CreateFactory(m.Value, Array.Empty<Type>())))
            .ToList();

        if (moduleFactories.Count == 0)
        {
            return sp =>
            {
                var app = (HttpApplication)factory(sp, null);
                app.Initialize(HttpModuleCollection.Empty, _state, eventInitializer);
                return app;
            };
        }

        return sp =>
        {
            var app = (HttpApplication)factory(sp, null);
            var modules = new (string, IHttpModule)[moduleFactories.Count];

            for (var i = 0; i < moduleFactories.Count; i++)
            {
                var module = (IHttpModule)moduleFactories[i].Item2(sp, null);
                modules[i] = (moduleFactories[i].Key, module);
            }

            app.Initialize(new(modules), _state, eventInitializer);

            return app;
        };
    }

    private Action<HttpApplication> GetEventInitializer(HttpApplicationOptions options)
    {
        var type = options.ApplicationType;
        var typeName = type.FullName ?? type.Name;
        Action<HttpApplication>? known = default;

        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            var state = EventParseState.None;

            if (UnsupportedEvents.Contains(method.Name))
            {
                state = EventParseState.NotSupported;
            }
            else if (KnownEvents.TryGetValue(method.Name, out var registration) && CreateHandler(method, ref state) is { } handler)
            {
                known += app => registration(app, handler(app));
            }

            if (state is EventParseState.Registered)
            {
                LogRegistration(typeName, method.Name);
            }
            else if (state is EventParseState.NotSupported)
            {
                LogUnsupported(typeName, method.Name);
            }
            else if (state is EventParseState.InvalidSignature)
            {
                LogInvalid(typeName, method.Name);
            }
        }

        return known ?? (_ => { });
    }

    private static BindableEventHandler? CreateHandler(MethodInfo method, ref EventParseState state)
    {
        var parameters = method.GetParameters();

        if (method.ReturnType == typeof(void))
        {
            if (parameters.Length == 0)
            {
                state = EventParseState.Registered;

                return app =>
                {
                    var d = method.CreateDelegate<Action>(app);

                    return (s, e) => d();
                };
            }

            if (parameters.Length == 2 && parameters[0].ParameterType == typeof(object) && parameters[1].ParameterType == typeof(EventArgs))
            {
                state = EventParseState.Registered;
                return app => method.CreateDelegate<EventHandler>(app);
            }
        }

        state = EventParseState.InvalidSignature;
        return null;
    }

    public void Dispose()
    {
        _state.Dispose();
    }

    private delegate EventHandler BindableEventHandler(HttpApplication app);

    private enum EventParseState
    {
        None,
        Registered,
        NotSupported,
        InvalidSignature,
    }
}
