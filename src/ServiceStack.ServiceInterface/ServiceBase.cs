﻿using System;
using System.Web;
using ServiceStack.CacheAccess;
using ServiceStack.CacheAccess.Providers;
using ServiceStack.Common;
using ServiceStack.Common.Web;
using ServiceStack.Logging;
using ServiceStack.Messaging;
using ServiceStack.Redis;
using ServiceStack.ServiceHost;
using ServiceStack.ServiceInterface.ServiceModel;
using ServiceStack.Text;
using ServiceStack.WebHost.Endpoints;
using ServiceStack.WebHost.Endpoints.Extensions;

namespace ServiceStack.ServiceInterface
{
    /// <summary>
    /// A Useful ServiceBase for all services with support for automatically serializing
    /// Exceptions into a common ResponseError DTO so errors can be handled generically by clients. 
    /// 
    /// If an 'IRedisClientsManager' is configured in your AppHost, service errors will
    /// also be maintained into a service specific and combined rolling error log.
    /// </summary>
    /// <typeparam name="TRequest"></typeparam>
    public abstract class ServiceBase<TRequest>
        : IService<TRequest>, IRequiresRequestContext, IServiceBase
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(ServiceBase<>));

        /// <summary>
        /// Service error logs are kept in 'urn:ServiceErrors:{ServiceName}'
        /// </summary>
        public const string UrnServiceErrorType = "ServiceErrors";

        /// <summary>
        /// Combined service error logs are maintained in 'urn:ServiceErrors:All'
        /// </summary>
        public const string CombinedServiceLogId = "All";


        /// <summary>
        /// Can be overriden to supply Custom 'ServiceName' error logs
        /// </summary>
        public virtual string ServiceName
        {
            get { return typeof(TRequest).Name; }
        }

        /// <summary>
        /// Access to the Applications ServiceStack AppHost Instance
        /// </summary>
        private IAppHost appHost;
        public virtual IAppHost AppHost
        {
            get
            {
                return appHost ?? EndpointHost.AppHost;
            }
            set
            {
                this.appHost = value;
            }
        }

        public IRequestContext RequestContext { get; set; }

        public ISessionFactory SessionFactory { get; set; }

        private ISession session;
        public ISession Session
        {
            get
            {
                if (SessionFactory == null)
                    SessionFactory = new SessionFactory(this.GetCacheClient());

                return session ?? (session =
                    SessionFactory.GetOrCreateSession(
                        RequestContext.Get<IHttpRequest>(),
                        RequestContext.Get<IHttpResponse>()
                    ));
            }
        }

        /// <summary>
        /// Resolve an alternate Web Service from ServiceStack's IOC container.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <returns></returns>
        public T ResolveService<T>()
        {
            var service = this.AppHost.TryResolve<T>();
            var requiresContext = service as IRequiresRequestContext;
            if (requiresContext != null)
            {
                requiresContext.RequestContext = this.RequestContext;
            }
            return service;
        }

        /// <summary>
        /// Maintains the current request DTO in this property
        /// </summary>
        protected TRequest CurrentRequestDto;

        /// <summary>
        /// Override to provide additional/less context about the Service Exception. 
        /// By default the request is serialized and appended to the ResponseStatus StackTrace.
        /// </summary>
        public virtual string GetRequestErrorBody()
        {
            var requestString = "";
            try
            {
                requestString = TypeSerializer.SerializeToString(CurrentRequestDto);
            }
            catch /*(Exception ignoreSerializationException)*/
            {
                //Serializing request successfully is not critical and only provides added error info
            }

            return string.Format("[{0}: {1}]:\n[REQUEST: {2}]", GetType().Name, DateTime.UtcNow, requestString);
        }

        public T TryResolve<T>()
        {
            return this.AppHost == null
                ? default(T)
                : this.AppHost.TryResolve<T>();
        }

        /// <summary>
        /// Single method sub classes should implement to execute the request
        /// </summary>
        /// <param name="request"></param>
        /// <returns></returns>
        protected abstract object Run(TRequest request);

        /// <summary>
        /// Called before the request is Executed. Override to enforce generic validation logic.
        /// </summary>
        /// <param name="request"></param>
		protected virtual void OnBeforeExecute(TRequest request) { }
		
		/// <summary>
		/// Called after the request is Executed. Override to decorate the response dto
		/// </summary>
		/// <param name="response"></param>
		/// <returns></returns>
		protected virtual object OnAfterExecute(object response)
		{
			return response;
		}
		
        /// <summary>
        /// Execute the request with the protected abstract Run() method in a managed scope by
        /// provide default handling of Service Exceptions by serializing exceptions in the response
        /// DTO and maintaining all service errors in a managed service-specific and combined rolling error logs
        /// </summary>
        /// <param name="request"></param>
        /// <returns></returns>
        public object Execute(TRequest request)
        {
            try
            {
                OnBeforeExecute(request);
				return OnAfterExecute(Run(request));
            }
            catch (Exception ex)
            {
                var result = HandleException(request, ex);

                if (result == null) throw;

                return result;
            }
        }

        protected virtual object HandleException(TRequest request, Exception ex)
        {
            var responseStatus = ResponseStatusTranslator.Instance.Parse(ex);

            if (EndpointHost.UserConfig.DebugMode)
            {
                // View stack trace in tests and on the client
                responseStatus.StackTrace = GetRequestErrorBody() + ex;
            }

            Log.Error("ServiceBase<TRequest>::Service Exception", ex);

            //If Redis is configured, maintain rolling service error logs in Redis (an in-memory datastore)
            var redisManager = TryResolve<IRedisClientsManager>();
            if (redisManager != null)
            {
                try
                {
                    //Get a thread-safe redis client from the client manager pool
                    using (var client = redisManager.GetClient())
                    {
                        //Get a client with a native interface for storing 'ResponseStatus' objects
                        var redis = client.GetTypedClient<ResponseStatus>();

                        //Store the errors in predictable Redis-named lists i.e. 
                        //'urn:ServiceErrors:{ServiceName}' and 'urn:ServiceErrors:All' 
                        var redisSeriviceErrorList = redis.Lists[UrnId.Create(UrnServiceErrorType, ServiceName)];
                        var redisCombinedErrorList = redis.Lists[UrnId.Create(UrnServiceErrorType, CombinedServiceLogId)];

                        //Append the error at the start of the service-specific and combined error logs.
                        redisSeriviceErrorList.Prepend(responseStatus);
                        redisCombinedErrorList.Prepend(responseStatus);

                        //Clip old error logs from the managed logs
                        const int rollingErrorCount = 1000;
                        redisSeriviceErrorList.Trim(0, rollingErrorCount);
                        redisCombinedErrorList.Trim(0, rollingErrorCount);
                    }
                }
                catch (Exception suppressRedisException)
                {
                    Log.Error("Could not append exception to redis service error logs", suppressRedisException);
                }
            }

            return ServiceUtils.CreateErrorResponse(request, ex, responseStatus);
        }
		
        protected HttpResult View(string viewName, object response)
        {
            return new HttpResult(response)
            {
                TemplateName = viewName
            };
        }

        /// <summary>
        /// The Deferred execution of ExecuteAsync(request)'s. 
        /// This request is typically invoked from a messaging queue service host.
        /// </summary>
        /// <param name="request"></param>
        public virtual object Execute(IMessage<TRequest> request)
        {
            return Execute(request.GetBody());
        }
    }
}