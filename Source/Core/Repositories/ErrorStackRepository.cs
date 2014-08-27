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
using System.Linq;
using System.Threading.Tasks;
using Exceptionless.Models;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using ServiceStack.CacheAccess;
using M = MongoDB.Driver.Builders;

namespace Exceptionless.Core {
    public class ErrorStackRepository : MongoRepositoryOwnedByOrganization<ErrorStack>, IErrorStackRepository {
        private readonly OrganizationRepository _organizationRepository;
        private readonly ProjectRepository _projectRepository;
        private readonly ErrorRepository _errorRepository;

        public ErrorStackRepository(MongoDatabase database, OrganizationRepository organizationRepository, ProjectRepository projectRepository, ErrorRepository errorRepository, ICacheClient cacheClient = null)
            : base(database, cacheClient) {
            _organizationRepository = organizationRepository;
            _projectRepository = projectRepository;
            _errorRepository = errorRepository;
        }

        public const string CollectionName = "errorstack";

        protected override string GetCollectionName() {
            return CollectionName;
        }

        public new static class FieldNames {
            public const string Id = "_id";
            public const string ProjectId = "pid";
            public const string OrganizationId = "oid";
            public const string SignatureHash = "hash";
            public const string FirstOccurrence = "fst";
            public const string LastOccurrence = "lst";
            public const string TotalOccurrences = "tot";
            public const string SignatureInfo = "sig";
            public const string SignatureInfo_Path = "sig.Path";
            public const string FixedInVersion = "fix";
            public const string DateFixed = "fdt";
            public const string Title = "tit";
            public const string Description = "dsc";
            public const string IsHidden = "hid";
            public const string IsRegressed = "regr";
            public const string DisableNotifications = "dnot";
            public const string OccurrencesAreCritical = "crit";
            public const string References = "refs";
            public const string Tags = "tag";
            public const string LastUpdated = "upd";
        }

        protected override void InitializeCollection(MongoCollection<ErrorStack> collection) {
            base.InitializeCollection(collection);

            collection.CreateIndex(M.IndexKeys.Ascending(FieldNames.ProjectId, FieldNames.SignatureHash), M.IndexOptions.SetUnique(true).SetBackground(true));
            collection.CreateIndex(M.IndexKeys.Descending(FieldNames.ProjectId, FieldNames.LastOccurrence), M.IndexOptions.SetBackground(true));
            collection.CreateIndex(M.IndexKeys.Descending(FieldNames.LastUpdated), M.IndexOptions.SetBackground(true));
            collection.CreateIndex(M.IndexKeys.Descending(FieldNames.ProjectId, FieldNames.IsHidden, FieldNames.DateFixed, FieldNames.SignatureInfo_Path), M.IndexOptions.SetBackground(true));
        }

        protected override void ConfigureClassMap(BsonClassMap<ErrorStack> cm) {
            base.ConfigureClassMap(cm);
            cm.GetMemberMap(c => c.ProjectId).SetRepresentation(BsonType.ObjectId).SetElementName(FieldNames.ProjectId);
            cm.GetMemberMap(c => c.SignatureHash).SetElementName(FieldNames.SignatureHash);
            cm.GetMemberMap(c => c.SignatureInfo).SetElementName(FieldNames.SignatureInfo);
            cm.GetMemberMap(c => c.FixedInVersion).SetElementName(FieldNames.FixedInVersion).SetIgnoreIfNull(true);
            cm.GetMemberMap(c => c.DateFixed).SetElementName(FieldNames.DateFixed).SetIgnoreIfNull(true);
            cm.GetMemberMap(c => c.Title).SetElementName(FieldNames.Title);
            cm.GetMemberMap(c => c.TotalOccurrences).SetElementName(FieldNames.TotalOccurrences);
            cm.GetMemberMap(c => c.FirstOccurrence).SetElementName(FieldNames.FirstOccurrence);
            cm.GetMemberMap(c => c.LastOccurrence).SetElementName(FieldNames.LastOccurrence);
            cm.GetMemberMap(c => c.Description).SetElementName(FieldNames.Description).SetIgnoreIfNull(true);
            cm.GetMemberMap(c => c.IsHidden).SetElementName(FieldNames.IsHidden).SetIgnoreIfDefault(true);
            cm.GetMemberMap(c => c.IsRegressed).SetElementName(FieldNames.IsRegressed).SetIgnoreIfDefault(true);
            cm.GetMemberMap(c => c.DisableNotifications).SetElementName(FieldNames.DisableNotifications).SetIgnoreIfDefault(true);
            cm.GetMemberMap(c => c.OccurrencesAreCritical).SetElementName(FieldNames.OccurrencesAreCritical).SetIgnoreIfDefault(true);
            cm.GetMemberMap(c => c.References).SetElementName(FieldNames.References).SetShouldSerializeMethod(obj => ((ErrorStack)obj).References.Any());
            cm.GetMemberMap(c => c.Tags).SetElementName(FieldNames.Tags).SetShouldSerializeMethod(obj => ((ErrorStack)obj).Tags.Any());
            cm.GetMemberMap(c => c.LastUpdated).SetElementName(FieldNames.LastUpdated);
        }

        public override void InvalidateCache(ErrorStack entity) {
            var originalStack = GetByIdCached(entity.Id);
            if (originalStack != null) {
                if (originalStack.DateFixed != entity.DateFixed) {
                    _errorRepository.UpdateFixedByStackId(entity.Id, entity.DateFixed.HasValue);
                    InvalidateFixedIdsCache(entity.ProjectId);
                }

                if (originalStack.IsHidden != entity.IsHidden) {
                    _errorRepository.UpdateHiddenByStackId(entity.Id, entity.IsHidden);
                    InvalidateHiddenIdsCache(entity.ProjectId);
                }

                InvalidateCache(String.Concat(entity.ProjectId, entity.SignatureHash));
            }

            base.InvalidateCache(entity);
        }

        public void InvalidateCache(string id, string signatureHash, string projectId) {
            InvalidateCache(id);
            InvalidateCache(String.Concat(projectId, signatureHash));
            InvalidateFixedIdsCache(projectId);
        }

        public void RemoveAllByProjectId(string projectId) {
            const int batchSize = 150;

            var stacks = Collection
                .Find(M.Query.EQ(FieldNames.ProjectId, new BsonObjectId(new ObjectId(projectId))))
                .SetLimit(batchSize)
                .SetFields(FieldNames.Id, FieldNames.OrganizationId, FieldNames.SignatureHash)
                .Select(es => new ErrorStack {
                    Id = es.Id,
                    ProjectId = projectId,
                    OrganizationId = es.OrganizationId,
                    SignatureHash = es.SignatureHash
                })
                .ToArray();

            while (stacks.Length > 0) {
                Delete(stacks);

                stacks = Collection
                    .Find(M.Query.EQ(FieldNames.ProjectId, new BsonObjectId(new ObjectId(projectId))))
                    .SetLimit(batchSize)
                    .SetFields(FieldNames.Id, FieldNames.OrganizationId, FieldNames.SignatureHash)
                    .Select(es => new ErrorStack {
                        Id = es.Id,
                        ProjectId = projectId,
                        OrganizationId = es.OrganizationId,
                        SignatureHash = es.SignatureHash
                    })
                    .ToArray();
            }

            _projectRepository.SetStats(projectId, errorCount: 0, stackCount: 0);
        }

        public override ErrorStack Add(ErrorStack stack, bool addToCache = false) {
            if (stack == null)
                throw new ArgumentNullException("stack");

            stack.LastUpdated = DateTime.UtcNow;
            return base.Add(stack, addToCache);
        }

        public override void Add(IEnumerable<ErrorStack> stacks, bool addToCache = false) {
            foreach (ErrorStack stack in stacks)
                Add(stack, addToCache);
        }

        public override ErrorStack Update(ErrorStack entity, bool addToCache = false) {
            entity.LastUpdated = DateTime.UtcNow;
            return base.Update(entity, addToCache);
        }

        public async Task RemoveAllByProjectIdAsync(string projectId) {
            await Task.Run(() => RemoveAllByProjectId(projectId));
        }

        public override void Delete(IEnumerable<ErrorStack> stacks) {
            var organizations = stacks.GroupBy(s => new {
                s.OrganizationId,
                s.ProjectId
            });
            foreach (var grouping in organizations) {
                var result = _collection.Remove(M.Query.In(FieldNames.Id, grouping.ToArray().Select(stack => new BsonObjectId(new ObjectId(stack.Id)))));

                if (result.DocumentsAffected <= 0)
                    continue;

                _organizationRepository.IncrementStats(grouping.Key.OrganizationId, stackCount: result.DocumentsAffected * -1);
                _projectRepository.IncrementStats(grouping.Key.ProjectId, stackCount: result.DocumentsAffected * -1);
            }

            foreach (ErrorStack entity in stacks) {
                // NOTE: We shouldn't need to call InvalidateHiddenId's here because they no longer exists.
                InvalidateCache(String.Concat(entity.ProjectId, entity.SignatureHash));
                base.InvalidateCache(entity);
            }
        }

        public void IncrementStats(string errorStackId, DateTime occurrenceDate) {
            // if total occurrences are zero (stack data was reset), then set first occurrence date
            _collection.Update(
                               M.Query.And(
                                           M.Query.EQ(FieldNames.Id, new BsonObjectId(new ObjectId(errorStackId))),
                                   M.Query.EQ(FieldNames.TotalOccurrences, new BsonInt32(0))
                                   ),
                M.Update.Set(FieldNames.FirstOccurrence, occurrenceDate).Set(FieldNames.LastUpdated, DateTime.UtcNow));

            // Only update the LastOccurrence if the new date is greater then the existing date.
            IMongoQuery query = M.Query.And(M.Query.EQ(FieldNames.Id, new BsonObjectId(new ObjectId(errorStackId))), M.Query.LT(FieldNames.LastOccurrence, occurrenceDate));
            M.UpdateBuilder update = M.Update.Inc(FieldNames.TotalOccurrences, 1).Set(FieldNames.LastOccurrence, occurrenceDate).Set(FieldNames.LastUpdated, DateTime.UtcNow);

            var result = _collection.Update(query, update);
            if (result.DocumentsAffected > 0)
                return;

            _collection.Update(M.Query.EQ(FieldNames.Id, new BsonObjectId(new ObjectId(errorStackId))), M.Update.Inc(FieldNames.TotalOccurrences, 1));
            InvalidateCache(errorStackId);
        }

        #region Queries

        public ErrorStackInfo GetErrorStackInfoBySignatureHash(string projectId, string signatureHash) {
            var result = Cache != null ? Cache.Get<ErrorStackInfo>(GetScopedCacheKey(String.Concat(projectId, signatureHash, "v2"))) : null;
            if (result == null) {
                result = Collection
                    .Find(M.Query.And(M.Query.EQ(FieldNames.ProjectId, new BsonObjectId(new ObjectId(projectId))), M.Query.EQ(FieldNames.SignatureHash, signatureHash)))
                    .SetLimit(1)
                    .SetFields(FieldNames.Id, FieldNames.DateFixed, FieldNames.OccurrencesAreCritical, FieldNames.IsHidden)
                    .Select(es => new ErrorStackInfo {
                        Id = es.Id,
                        DateFixed = es.DateFixed,
                        OccurrencesAreCritical = es.OccurrencesAreCritical,
                        IsHidden = es.IsHidden
                    })
                    .FirstOrDefault();

                if (Cache != null && result != null)
                    Cache.Set(GetScopedCacheKey(String.Concat(projectId, signatureHash, "v2")), result, TimeSpan.FromMinutes(5));
            }

            // add extra info to object that's not stored in the cache memory
            if (result != null)
                result.SignatureHash = signatureHash;

            return result;
        }

        public string[] GetHiddenIds(string projectId) {
            var result = Cache != null ? Cache.Get<string[]>(GetScopedCacheKey(String.Concat(projectId, "@__HIDDEN"))) : null;
            if (result == null) {
                result = Collection
                    .Find(M.Query.And(M.Query.EQ(FieldNames.ProjectId, new BsonObjectId(new ObjectId(projectId))), M.Query.EQ(FieldNames.IsHidden, BsonBoolean.True)))
                    .SetFields(FieldNames.Id)
                    .Select(err => err.Id)
                    .ToArray();
                if (Cache != null)
                    Cache.Set(GetScopedCacheKey(String.Concat(projectId, "@__HIDDEN")), result, TimeSpan.FromMinutes(5));
            }

            return result;
        }

        public void InvalidateHiddenIdsCache(string projectId) {
            Cache.Remove(GetScopedCacheKey(String.Concat(projectId, "@__HIDDEN")));
        }

        public string[] GetFixedIds(string projectId) {
            var result = Cache != null ? Cache.Get<string[]>(GetScopedCacheKey(String.Concat(projectId, "@__FIXED"))) : null;
            if (result == null) {
                result = Collection
                    .Find(M.Query.And(M.Query.EQ(FieldNames.ProjectId, new BsonObjectId(new ObjectId(projectId))), M.Query.Exists(FieldNames.DateFixed)))
                    .SetFields(FieldNames.Id)
                    .Select(err => err.Id)
                    .ToArray();
                if (Cache != null)
                    Cache.Set(GetScopedCacheKey(String.Concat(projectId, "@__FIXED")), result, TimeSpan.FromMinutes(5));
            }

            return result;
        }

        public void InvalidateFixedIdsCache(string projectId) {
            Cache.Remove(GetScopedCacheKey(String.Concat(projectId, "@__FIXED")));
        }

        public string[] GetNotFoundIds(string projectId) {
            var result = Cache != null ? Cache.Get<string[]>(GetScopedCacheKey(String.Concat(projectId, "@__NOTFOUND"))) : null;
            if (result == null) {
                result = Collection
                    .Find(M.Query.And(M.Query.EQ(FieldNames.ProjectId, new BsonObjectId(new ObjectId(projectId))), M.Query.Exists(FieldNames.SignatureInfo_Path)))
                    .SetFields(FieldNames.Id)
                    .Select(err => err.Id)
                    .ToArray();
                if (Cache != null)
                    Cache.Set(GetScopedCacheKey(String.Concat(projectId, "@__NOTFOUND")), result, TimeSpan.FromMinutes(5));
            }

            return result;
        }

        public void InvalidateNotFoundIdsCache(string projectId) {
            Cache.Remove(GetScopedCacheKey(String.Concat(projectId, "@__NOTFOUND")));
        }

        public IEnumerable<ErrorStack> GetMostRecent(string projectId, DateTime utcStart, DateTime utcEnd, int? skip, int? take, out long count, bool includeHidden = false, bool includeFixed = false, bool includeNotFound = true) {
            var conditions = new List<IMongoQuery> {
                M.Query.EQ(FieldNames.ProjectId, new BsonObjectId(new ObjectId(projectId))),
                M.Query.GTE(FieldNames.LastOccurrence, utcStart),
                M.Query.LTE(FieldNames.LastOccurrence, utcEnd)
            };

            if (!includeFixed)
                conditions.Add(M.Query.NotExists(FieldNames.DateFixed));

            if (!includeHidden)
                conditions.Add(M.Query.NE(FieldNames.IsHidden, true));

            if (!includeNotFound)
                conditions.Add(M.Query.NotExists(FieldNames.SignatureInfo_Path));

            var cursor = _collection.FindAs<ErrorStack>(M.Query.And(conditions));
            cursor.SetSortOrder(M.SortBy.Descending(FieldNames.LastOccurrence));

            if (skip.HasValue)
                cursor.SetSkip(skip.Value);

            if (take.HasValue)
                cursor.SetLimit(take.Value);

            count = cursor.Count();
            return cursor;
        }

        public IEnumerable<ErrorStack> GetNew(string projectId, DateTime utcStart, DateTime utcEnd, int? skip, int? take, out long count, bool includeHidden = false, bool includeFixed = false, bool includeNotFound = true) {
            var conditions = new List<IMongoQuery> {
                M.Query.EQ(FieldNames.ProjectId, new BsonObjectId(new ObjectId(projectId))),
                M.Query.GTE(FieldNames.FirstOccurrence, utcStart),
                M.Query.LTE(FieldNames.FirstOccurrence, utcEnd)
            };

            if (!includeFixed)
                conditions.Add(M.Query.NotExists(FieldNames.DateFixed));

            if (!includeHidden)
                conditions.Add(M.Query.NE(FieldNames.IsHidden, true));

            if (!includeNotFound)
                conditions.Add(M.Query.NotExists(FieldNames.SignatureInfo_Path));

            var cursor = _collection.FindAs<ErrorStack>(M.Query.And(conditions));
            cursor.SetSortOrder(M.SortBy.Descending(FieldNames.FirstOccurrence));

            if (skip.HasValue)
                cursor.SetSkip(skip.Value);

            if (take.HasValue)
                cursor.SetLimit(take.Value);

            count = cursor.Count();
            return cursor;
        }

        #endregion
    }
}