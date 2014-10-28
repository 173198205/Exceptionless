﻿#region Copyright 2014 Exceptionless

// This program is free software: you can redistribute it and/or modify it 
// under the terms of the GNU Affero General Public License as published 
// by the Free Software Foundation, either version 3 of the License, or 
// (at your option) any later version.
// 
//     http://www.gnu.org/licenses/agpl-3.0.html

#endregion

using System;
using System.Collections.Generic;
using CodeSmith.Core.Component;
using CodeSmith.Core.Dependency;
using Exceptionless.Core.AppStats;
using Exceptionless.Models;

namespace Exceptionless.Core.Pipeline {
    public class ErrorPipeline : PipelineBase<ErrorPipelineContext, ErrorPipelineActionBase> {
        private readonly IAppStatsClient _stats;

        public ErrorPipeline(IDependencyResolver dependencyResolver, IAppStatsClient stats) : base(dependencyResolver) {
            _stats = stats;
        }

        public void Run(Error error) {
            var ctx = new ErrorPipelineContext(error);
            Run(ctx);
        }

        protected override void Run(ErrorPipelineContext context, IEnumerable<Type> actionTypes) {
            base.Run(context, actionTypes);
            if (context.IsCancelled)
                _stats.Counter(StatNames.ErrorsProcessingCancelled);
        }

        public void Run(IEnumerable<Error> errors) {
            foreach (Error error in errors)
                Run(error);
        }
    }
}