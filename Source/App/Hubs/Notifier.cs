﻿#region Copyright 2014 Exceptionless

// This program is free software: you can redistribute it and/or modify it 
// under the terms of the GNU Affero General Public License as published 
// by the Free Software Foundation, either version 3 of the License, or 
// (at your option) any later version.
// 
//     http://www.gnu.org/licenses/agpl-3.0.html

#endregion

using System;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Mvc;
using CodeSmith.Core.Extensions;
using Exceptionless.Core;
using Exceptionless.Core.Authorization;
using Exceptionless.Core.Pipeline;
using Microsoft.AspNet.SignalR;
using ServiceStack.CacheAccess;
using ServiceStack.Redis;

namespace Exceptionless.App.Hubs {
    public class Notifier : Hub {
        public override Task OnConnected() {
            var user = Context.User as ExceptionlessPrincipal;
            if (user == null)
                return base.OnConnected();

            foreach (string organizationId in user.UserEntity.OrganizationIds)
                Groups.Add(Context.ConnectionId, organizationId);

            return base.OnConnected();
        }
    }

    public class NotificationSender {
        private readonly ICacheClient _cacheClient;
        private readonly IRedisClientsManager _redisClientsManager;
        private const int THROTTLE_NOTIFICATIONS_DELAY_IN_SECONDS = 5;

        public NotificationSender(ICacheClient cacheClient, IRedisClientsManager redisClientsManager) {
            _cacheClient = cacheClient;
            _redisClientsManager = redisClientsManager;
        }

        public event EventHandler Ping;

        public void Listen() {
            Task.Factory.StartNew(() => {
                using (IRedisClient client = _redisClientsManager.GetReadOnlyClient()) {
                    using (IRedisSubscription subscription = client.CreateSubscription()) {
                        subscription.OnMessage = (channel, msg) => {
                            string[] parts = msg.Split(':');
                            if (parts.Length < 1)
                                return;

                            switch (parts[0]) {
                                case "ping":
                                    Ping(this, EventArgs.Empty);
                                    break;
                                case "overlimit":
                                    if (parts.Length != 3)
                                        return;

                                    if (parts[1] == "hr")
                                        WentOverHourlyLimit(parts[2]);
                                    else
                                        WentOverMonthlyLimit(parts[2]);
                                        
                                    break;
                                default: // error occurred
                                    if (parts.Length != 6)
                                        return;

                                    bool isHidden;
                                    Boolean.TryParse(parts[3], out isHidden);

                                    bool isFixed;
                                    Boolean.TryParse(parts[4], out isFixed);

                                    bool is404;
                                    Boolean.TryParse(parts[5], out is404);

                                    NewError(parts[0], parts[1], parts[2], isHidden, isFixed, is404);
                                    break;
                            }
                        };
                        RetryUtil.Retry(() => subscription.SubscribeToChannels(NotifySignalRAction.NOTIFICATION_CHANNEL_KEY));
                    }
                }
            });
        }

        private static DateTime _lastListenerCheck;

        public void EnsureListening() {
            // Check if the notifier listener is listening every 10 seconds.
            if (!(DateTime.Now.Subtract(_lastListenerCheck).TotalSeconds > 10))
                return;

            if (!IsListening())
                Listen();

            _lastListenerCheck = DateTime.Now;
        }

        public bool IsListening() {
            try {
                var resetEvent = new AutoResetEvent(false);
                EventHandler handler = (sender, e) => resetEvent.Set();
                Ping -= handler;
                Ping += handler;

                using (IRedisClient client = _redisClientsManager.GetClient())
                    client.PublishMessage(NotifySignalRAction.NOTIFICATION_CHANNEL_KEY, "ping");

                bool success = resetEvent.WaitOne(500); 
                Ping -= handler;

                return success;
            } catch (Exception) {
                return false;
            }
        }

        public void PlanChanged(string organizationId) {
            if (!Settings.Current.EnableSignalR)
                return;

            if (GlobalHost.ConnectionManager == null)
                return;

            IHubContext context = GlobalHost.ConnectionManager.GetHubContext<Notifier>();
            if (context == null)
                return;

            // Throttle notifications to one every x seconds.
            var lastNotification = _cacheClient.Get<DateTime>(String.Concat("SignalR.Org.", organizationId));
            if (!(DateTime.Now.Subtract(lastNotification).TotalSeconds >= THROTTLE_NOTIFICATIONS_DELAY_IN_SECONDS))
                return;

            context.Clients.Group(organizationId).planChanged(organizationId);
            _cacheClient.Set(String.Concat("SignalR.Org.", organizationId), DateTime.Now);
        }

        public void OrganizationUpdated(string organizationId) {
            if (!Settings.Current.EnableSignalR)
                return;

            if (GlobalHost.ConnectionManager == null)
                return;

            IHubContext context = GlobalHost.ConnectionManager.GetHubContext<Notifier>();
            if (context == null)
                return;

            // Throttle notifications to one every x seconds.
            var lastNotification = _cacheClient.Get<DateTime>(String.Concat("SignalR.Org.", organizationId));
            if (!(DateTime.Now.Subtract(lastNotification).TotalSeconds >= THROTTLE_NOTIFICATIONS_DELAY_IN_SECONDS))
                return;

            context.Clients.Group(organizationId).organizationUpdated(organizationId);
            _cacheClient.Set(String.Concat("SignalR.Org.", organizationId), DateTime.Now);
        }

        public void ProjectUpdated(string organizationId, string projectId) {
            if (!Settings.Current.EnableSignalR)
                return;

            if (GlobalHost.ConnectionManager == null)
                return;

            IHubContext context = GlobalHost.ConnectionManager.GetHubContext<Notifier>();
            if (context == null)
                return;

            // Throttle notifications to one every x seconds.
            var lastNotification = _cacheClient.Get<DateTime>(String.Concat("SignalR.Org.", organizationId));
            if (!(DateTime.Now.Subtract(lastNotification).TotalSeconds >= THROTTLE_NOTIFICATIONS_DELAY_IN_SECONDS))
                return;

            context.Clients.Group(organizationId).projectUpdated(projectId);
            _cacheClient.Set(String.Concat("SignalR.Org.", organizationId), DateTime.Now);
        }

        public void StackUpdated(string organizationId, string projectId, string stackId, bool isHidden, bool isFixed, bool is404) {
            if (!Settings.Current.EnableSignalR)
                return;

            if (GlobalHost.ConnectionManager == null)
                return;

            IHubContext context = GlobalHost.ConnectionManager.GetHubContext<Notifier>();
            if (context == null)
                return;

            // Throttle notifications to one every x seconds.
            var lastNotification = _cacheClient.Get<DateTime>(String.Concat("SignalR.Org.", organizationId));
            if (!(DateTime.Now.Subtract(lastNotification).TotalSeconds >= THROTTLE_NOTIFICATIONS_DELAY_IN_SECONDS))
                return;

            context.Clients.Group(organizationId).stackUpdated(projectId, stackId, isHidden, isFixed, is404);
            _cacheClient.Set(String.Concat("SignalR.Org.", organizationId), DateTime.Now);
        }

        public void NewError(string organizationId, string projectId, string stackId, bool isHidden, bool isFixed, bool is404) {
            if (!Settings.Current.EnableSignalR)
                return;

            if (GlobalHost.ConnectionManager == null)
                return;

            IHubContext context = GlobalHost.ConnectionManager.GetHubContext<Notifier>();

            if (context == null)
                return;

            // Throttle notifications to one every x seconds.
            var lastNotification = _cacheClient.Get<DateTime>(String.Concat("SignalR.Org.", organizationId));
            if (!(DateTime.Now.Subtract(lastNotification).TotalSeconds >= THROTTLE_NOTIFICATIONS_DELAY_IN_SECONDS))
                return;

            context.Clients.Group(organizationId).newError(projectId, stackId, isHidden, isFixed, is404);
            _cacheClient.Set(String.Concat("SignalR.Org.", organizationId), DateTime.Now);
        }

        public void WentOverHourlyLimit(string organizationId) {
            if (!Settings.Current.EnableSignalR)
                return;

            if (GlobalHost.ConnectionManager == null)
                return;

            IHubContext context = GlobalHost.ConnectionManager.GetHubContext<Notifier>();

            if (context == null)
                return;

            context.Clients.Group(organizationId).wentOverHourlyLimit(organizationId);
        }

        public void WentOverMonthlyLimit(string organizationId) {
            if (!Settings.Current.EnableSignalR)
                return;

            if (GlobalHost.ConnectionManager == null)
                return;

            IHubContext context = GlobalHost.ConnectionManager.GetHubContext<Notifier>();

            if (context == null)
                return;

            context.Clients.Group(organizationId).wentOverMonthlyLimit(organizationId);
        }
    }
}