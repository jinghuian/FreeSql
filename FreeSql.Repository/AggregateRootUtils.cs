﻿using FreeSql;
using FreeSql.Extensions.EntityUtil;
using FreeSql.Internal;
using FreeSql.Internal.CommonProvider;
using FreeSql.Internal.Model;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;

namespace FreeSql
{
    public static class AggregateRootUtils
    {
        public static void CompareEntityValue(IFreeSql fsql, Type rootEntityType, object rootEntityBefore, object rootEntityAfter, string rootNavigatePropertyName, AggregateRootTrackingChangeInfo tracking)
        {
            Dictionary<Type, Dictionary<string, bool>> ignores = new Dictionary<Type, Dictionary<string, bool>>();
            LocalCompareEntityValue(rootEntityType, rootEntityBefore, rootEntityAfter, rootNavigatePropertyName);
            ignores.Clear();

            void LocalCompareEntityValue(Type entityType, object entityBefore, object entityAfter, string navigatePropertyName)
            {
                if (entityType == null) entityType = entityBefore?.GetType() ?? entityAfter?.GetType();

                if (entityBefore != null)
                {
                    var stateKey = $":before://{fsql.GetEntityKeyString(entityType, entityBefore, false)}";
                    if (ignores.TryGetValue(entityType, out var stateKeys) == false) ignores.Add(entityType, stateKeys = new Dictionary<string, bool>());
                    if (stateKeys.ContainsKey(stateKey)) return;
                    stateKeys.Add(stateKey, true);
                }
                if (entityAfter != null)
                {
                    var stateKey = $":after://{fsql.GetEntityKeyString(entityType, entityAfter, false)}";
                    if (ignores.TryGetValue(entityType, out var stateKeys) == false) ignores.Add(entityType, stateKeys = new Dictionary<string, bool>());
                    if (stateKeys.ContainsKey(stateKey)) return;
                    stateKeys.Add(stateKey, true);
                }

                var table = fsql.CodeFirst.GetTableByEntity(entityType);
                if (table == null) return;
                if (entityBefore == null && entityAfter == null) return;
                if (entityBefore == null && entityAfter != null)
                {
                    tracking.InsertLog.Add(NativeTuple.Create(entityType, entityAfter));
                    return;
                }
                if (entityBefore != null && entityAfter == null)
                {
                    tracking.DeleteLog.Add(NativeTuple.Create(entityType, new[] { entityBefore }));
                    NavigateReader(fsql, entityType, entityBefore, (path, tr, ct, stackvs) =>
                    {
                        var dellist = stackvs.Last() as object[] ?? new[] { stackvs.Last() };
                        tracking.DeleteLog.Add(NativeTuple.Create(ct, dellist));
                    });
                    return;
                }
                var changes = new List<string>();
                foreach (var col in table.ColumnsByCs.Values)
                {
                    if (table.ColumnsByCsIgnore.ContainsKey(col.CsName)) continue;
                    if (table.ColumnsByCs.ContainsKey(col.CsName))
                    {
                        if (col.Attribute.IsVersion) continue;
                        var propvalBefore = table.GetPropertyValue(entityBefore, col.CsName);
                        var propvalAfter = table.GetPropertyValue(entityAfter, col.CsName);
                        if (object.Equals(propvalBefore, propvalAfter) == false) changes.Add(col.CsName);
                        continue;
                    }
                }
                if (changes.Any())
                    tracking.UpdateLog.Add(NativeTuple.Create(entityType, entityBefore, entityAfter, changes));

                foreach (var tr in table.GetAllTableRef().OrderBy(a => a.Value.RefType).ThenBy(a => a.Key))
                {
                    var tbref = tr.Value;
                    if (tbref.Exception != null) continue;
                    if (table.Properties.TryGetValue(tr.Key, out var prop) == false) continue;
                    if (navigatePropertyName != null && prop.Name != navigatePropertyName) continue;
                    var propvalBefore = table.GetPropertyValue(entityBefore, prop.Name);
                    var propvalAfter = table.GetPropertyValue(entityAfter, prop.Name);
                    switch (tbref.RefType)
                    {
                        case TableRefType.OneToOne:
                            SetNavigateRelationshipValue(fsql, tbref, table.Type, entityBefore, propvalBefore);
                            SetNavigateRelationshipValue(fsql, tbref, table.Type, entityAfter, propvalAfter);
                            LocalCompareEntityValue(tbref.RefEntityType, propvalBefore, propvalAfter, null);
                            break;
                        case TableRefType.OneToMany:
                            SetNavigateRelationshipValue(fsql, tbref, table.Type, entityBefore, propvalBefore);
                            SetNavigateRelationshipValue(fsql, tbref, table.Type, entityAfter, propvalAfter);
                            LocalCompareEntityValueCollection(tbref, propvalBefore as IEnumerable, propvalAfter as IEnumerable);
                            break;
                        case TableRefType.ManyToMany:
                            var middleValuesBefore = GetManyToManyObjects(fsql, table, tbref, entityBefore, prop);
                            var middleValuesAfter = GetManyToManyObjects(fsql, table, tbref, entityAfter, prop);
                            LocalCompareEntityValueCollection(tbref, middleValuesBefore as IEnumerable, middleValuesAfter as IEnumerable);
                            break;
                        case TableRefType.PgArrayToMany:
                        case TableRefType.ManyToOne: //不属于聚合根
                            break;
                    }
                }
            }
            void LocalCompareEntityValueCollection(TableRef tbref, IEnumerable collectionBefore, IEnumerable collectionAfter)
            {
                var elementType = tbref.RefType == TableRefType.ManyToMany ? tbref.RefMiddleEntityType : tbref.RefEntityType;
                if (collectionBefore == null && collectionAfter == null) return;
                if (collectionBefore == null && collectionAfter != null)
                {
                    foreach (var item in collectionAfter)
                        tracking.InsertLog.Add(NativeTuple.Create(elementType, item));
                    return;
                }
                if (collectionBefore != null && collectionAfter == null)
                {
                    //foreach (var item in collectionBefore as IEnumerable)
                    //{
                    //    changelog.DeleteLog.Add(NativeTuple.Create(elementType, new[] { item }));
                    //    NavigateReader(fsql, elementType, item, (path, tr, ct, stackvs) =>
                    //    {
                    //        var dellist = stackvs.Last() as object[] ?? new [] { stackvs.Last() };
                    //        changelog.DeleteLog.Add(NativeTuple.Create(ct, dellist));
                    //    });
                    //}
                    return;
                }
                Dictionary<string, object> dictBefore = new Dictionary<string, object>();
                Dictionary<string, object> dictAfter = new Dictionary<string, object>();
                foreach (var item in collectionBefore as IEnumerable)
                {
                    var key = fsql.GetEntityKeyString(elementType, item, false);
                    if (key != null) dictBefore.Add(key, item);
                }
                foreach (var item in collectionAfter as IEnumerable)
                {
                    var key = fsql.GetEntityKeyString(elementType, item, false);
                    if (key != null) tracking.InsertLog.Add(NativeTuple.Create(elementType, item));
                    else dictAfter.Add(key, item);
                }
                foreach (var key in dictBefore.Keys.ToArray())
                {
                    if (dictAfter.ContainsKey(key) == false)
                    {
                        var value = dictBefore[key];
                        tracking.DeleteLog.Add(NativeTuple.Create(elementType, new[] { value }));
                        NavigateReader(fsql, elementType, value, (path, tr, ct, stackvs) =>
                        {
                            var dellist = stackvs.Last() as object[] ?? new[] { stackvs.Last() };
                            tracking.DeleteLog.Add(NativeTuple.Create(ct, dellist));
                        });
                        dictBefore.Remove(key);
                    }
                }
                foreach (var key in dictAfter.Keys.ToArray())
                {
                    if (dictBefore.ContainsKey(key) == false)
                    {
                        tracking.InsertLog.Add(NativeTuple.Create(elementType, dictAfter[key]));
                        dictAfter.Remove(key);
                    }
                }
                foreach (var key in dictBefore.Keys)
                    LocalCompareEntityValue(elementType, dictBefore[key], dictAfter[key], null);
            }
        }

        public static void NavigateReader(IFreeSql fsql, Type rootType, object rootEntity, Action<string, TableRef, Type, List<object>> callback)
        {
            Dictionary<Type, Dictionary<string, bool>> ignores = new Dictionary<Type, Dictionary<string, bool>>();
            var statckPath = new Stack<string>();
            var stackValues = new List<object>();
            statckPath.Push("_");
            stackValues.Add(rootEntity);
            LocalNavigateReader(rootType, rootEntity);
            ignores.Clear();

            void LocalNavigateReader(Type entityType, object entity)
            {
                if (entity == null) return;
                if (entityType == null) entityType = entity.GetType();
                var table = fsql.CodeFirst.GetTableByEntity(entityType);
                if (table == null) return;

                var stateKey = fsql.GetEntityKeyString(entityType, entity, false);
                if (ignores.TryGetValue(entityType, out var stateKeys) == false) ignores.Add(entityType, stateKeys = new Dictionary<string, bool>());
                if (stateKeys.ContainsKey(stateKey)) return;
                stateKeys.Add(stateKey, true);

                foreach (var tr in table.GetAllTableRef().OrderBy(a => a.Value.RefType).ThenBy(a => a.Key))
                {
                    var tbref = tr.Value;
                    if (tbref.Exception != null) continue;
                    if (table.Properties.TryGetValue(tr.Key, out var prop) == false) continue;
                    switch (tbref.RefType)
                    {
                        case TableRefType.OneToOne:
                            var propval = table.GetPropertyValue(entity, prop.Name);
                            if (propval == null) continue;
                            statckPath.Push(prop.Name);
                            stackValues.Add(propval);
                            SetNavigateRelationshipValue(fsql, tbref, table.Type, entity, propval);
                            callback?.Invoke(string.Join(".", statckPath), tbref, tbref.RefEntityType, stackValues);
                            LocalNavigateReader(tbref.RefEntityType, propval);
                            stackValues.RemoveAt(stackValues.Count - 1);
                            statckPath.Pop();
                            break;
                        case TableRefType.OneToMany:
                            var propvalOtm = table.GetPropertyValue(entity, prop.Name);
                            if (propvalOtm == null) continue;
                            SetNavigateRelationshipValue(fsql, tbref, table.Type, entity, propvalOtm);
                            var propvalOtmList = new List<object>();
                            foreach (var val in propvalOtm as IEnumerable)
                                propvalOtmList.Add(val);
                            statckPath.Push($"{prop.Name}[]");
                            stackValues.Add(propvalOtmList.ToArray());
                            callback?.Invoke(string.Join(".", statckPath), tbref, tbref.RefEntityType, stackValues);
                            foreach (var val in propvalOtm as IEnumerable)
                                LocalNavigateReader(tbref.RefEntityType, val);
                            stackValues.RemoveAt(stackValues.Count - 1);
                            statckPath.Pop();
                            break;
                        case TableRefType.ManyToMany:
                            var middleValues = GetManyToManyObjects(fsql, table, tbref, entity, prop).ToArray();
                            if (middleValues == null) continue;
                            statckPath.Push($"{prop.Name}[]");
                            stackValues.Add(middleValues);
                            callback?.Invoke(string.Join(".", statckPath), tbref, tbref.RefEntityType, stackValues);
                            stackValues.RemoveAt(stackValues.Count - 1);
                            statckPath.Pop();
                            break;
                        case TableRefType.PgArrayToMany:
                        case TableRefType.ManyToOne: //不属于聚合根
                            break;
                    }
                }
            }
        }

        public static void MapEntityValue(IFreeSql fsql, Type rootEntityType, object rootEntityFrom, object rootEntityTo)
        {
            Dictionary<Type, Dictionary<string, bool>> ignores = new Dictionary<Type, Dictionary<string, bool>>();
            LocalMapEntityValue(rootEntityType, rootEntityFrom, rootEntityTo);
            ignores.Clear();

            void LocalMapEntityValue(Type entityType, object entityFrom, object entityTo)
            {
                if (entityFrom == null || entityTo == null) return;
                if (entityType == null) entityType = entityFrom?.GetType() ?? entityTo?.GetType();
                var table = fsql.CodeFirst.GetTableByEntity(entityType);
                if (table == null) return;

                var stateKey = fsql.GetEntityKeyString(entityType, entityFrom, false);
                if (ignores.TryGetValue(entityType, out var stateKeys) == false) ignores.Add(entityType, stateKeys = new Dictionary<string, bool>());
                if (stateKeys.ContainsKey(stateKey)) return;
                stateKeys.Add(stateKey, true);

                foreach (var prop in table.Properties.Values)
                {
                    if (table.ColumnsByCsIgnore.ContainsKey(prop.Name)) continue;
                    if (table.ColumnsByCs.ContainsKey(prop.Name))
                    {
                        table.SetPropertyValue(entityTo, prop.Name, table.GetPropertyValue(entityFrom, prop.Name));
                        continue;
                    }
                    var tbref = table.GetTableRef(prop.Name, false);
                    if (tbref == null) continue;
                    var propvalFrom = EntityUtilExtensions.GetEntityValueWithPropertyName(fsql, entityType, entityFrom, prop.Name);
                    if (propvalFrom == null)
                    {
                        EntityUtilExtensions.SetEntityValueWithPropertyName(fsql, entityType, entityTo, prop.Name, null);
                        return;
                    }
                    switch (tbref.RefType)
                    {
                        case TableRefType.OneToOne:
                            var propvalTo = tbref.RefEntityType.CreateInstanceGetDefaultValue();
                            SetNavigateRelationshipValue(fsql, tbref, table.Type, entityFrom, propvalFrom);
                            LocalMapEntityValue(tbref.RefEntityType, propvalFrom, propvalTo);
                            EntityUtilExtensions.SetEntityValueWithPropertyName(fsql, entityType, entityTo, prop.Name, propvalTo);
                            break;
                        case TableRefType.OneToMany:
                            SetNavigateRelationshipValue(fsql, tbref, table.Type, entityFrom, propvalFrom);
                            LocalMapEntityValueCollection(entityType, entityFrom, entityTo, tbref, propvalFrom as IEnumerable, prop, true);
                            break;
                        case TableRefType.ManyToMany:
                            LocalMapEntityValueCollection(entityType, entityFrom, entityTo, tbref, propvalFrom as IEnumerable, prop, false);
                            break;
                        case TableRefType.PgArrayToMany:
                        case TableRefType.ManyToOne: //不属于聚合根
                            break;
                    }
                }
            }
            void LocalMapEntityValueCollection(Type entityType, object entityFrom, object entityTo, TableRef tbref, IEnumerable propvalFrom, PropertyInfo prop, bool cascade)
            {
                var propvalTo = typeof(List<>).MakeGenericType(tbref.RefEntityType).CreateInstanceGetDefaultValue();
                var propvalToIList = propvalTo as IList;
                foreach (var fromItem in propvalFrom)
                {
                    var toItem = tbref.RefEntityType.CreateInstanceGetDefaultValue();
                    if (cascade) LocalMapEntityValue(tbref.RefEntityType, fromItem, toItem);
                    else EntityUtilExtensions.MapEntityValue(fsql, tbref.RefEntityType, fromItem, toItem);
                    propvalToIList.Add(toItem);
                }
                var propvalType = prop.PropertyType.GetGenericTypeDefinition();
                if (propvalType == typeof(List<>) || propvalType == typeof(ICollection<>))
                    EntityUtilExtensions.SetEntityValueWithPropertyName(fsql, entityType, entityTo, prop.Name, propvalTo);
                else if (propvalType == typeof(ObservableCollection<>))
                {
                    //var propvalTypeOcCtor = typeof(ObservableCollection<>).MakeGenericType(tbref.RefEntityType).GetConstructor(new[] { typeof(List<>).MakeGenericType(tbref.RefEntityType) });
                    var propvalTypeOc = Activator.CreateInstance(typeof(ObservableCollection<>).MakeGenericType(tbref.RefEntityType), new object[] { propvalTo });
                    EntityUtilExtensions.SetEntityValueWithPropertyName(fsql, entityType, entityTo, prop.Name, propvalTypeOc);
                }
            }
        }

        static ConcurrentDictionary<Type, ConcurrentDictionary<Type, Action<ISelect0>>> _dicGetAutoIncludeQuery = new ConcurrentDictionary<Type, ConcurrentDictionary<Type, Action<ISelect0>>>();
        public static ISelect<TEntity> GetAutoIncludeQuery<TEntity>(ISelect<TEntity> select)
        {
            var select0p = select as Select0Provider;
            var table0Type = select0p._tables[0].Table.Type;
            var func = _dicGetAutoIncludeQuery.GetOrAdd(typeof(TEntity), t => new ConcurrentDictionary<Type, Action<ISelect0>>()).GetOrAdd(table0Type, t =>
             {
                 var parmExp1 = Expression.Parameter(typeof(ISelect0));
                 var parmNavigateParameterExp = Expression.Parameter(typeof(TEntity), "a");
                 var parmQueryExp = Expression.Convert(parmExp1, typeof(ISelect<>).MakeGenericType(typeof(TEntity)));
                 var exp = LocalGetAutoIncludeQuery(parmQueryExp, 1, t, parmNavigateParameterExp, parmNavigateParameterExp, new Stack<Type>());
                 return Expression.Lambda<Action<ISelect0>>(exp, parmExp1).Compile();
             });
            func(select);
            return select;
            Expression LocalGetAutoIncludeQuery(Expression queryExp, int depth, Type entityType, ParameterExpression navigateParameterExp, Expression navigatePathExp, Stack<Type> ignores)
            {
                if (ignores.Any(a => a == entityType)) return queryExp;
                ignores.Push(entityType);
                var table = select0p._orm.CodeFirst.GetTableByEntity(entityType);
                if (table == null) return queryExp;
                foreach (var tr in table.GetAllTableRef().OrderBy(a => a.Value.RefType).ThenBy(a => a.Key))
                {
                    var tbref = tr.Value;
                    if (tbref.Exception != null) continue;
                    if (table.Properties.TryGetValue(tr.Key, out var prop) == false) continue;
                    Expression navigateExp = Expression.MakeMemberAccess(navigatePathExp, prop);
                    //var lambdaAlias = (char)((byte)'a' + (depth - 1));
                    switch (tbref.RefType)
                    {
                        case TableRefType.OneToOne:
                            if (ignores.Any(a => a == tbref.RefEntityType)) break;
                            LocalInclude(tbref, navigateExp);
                            queryExp = LocalGetAutoIncludeQuery(queryExp, depth, tbref.RefEntityType, navigateParameterExp, navigateExp, ignores);
                            break;
                        case TableRefType.OneToMany:
                            LocalIncludeMany(tbref, navigateExp, true);
                            break;
                        case TableRefType.ManyToMany:
                            LocalIncludeMany(tbref, navigateExp, false);
                            break;
                        case TableRefType.PgArrayToMany:
                            break;
                        case TableRefType.ManyToOne: //不属于聚合根
                            break;
                    }
                }
                ignores.Pop();
                return queryExp;
                void LocalInclude(TableRef tbref, Expression exp)
                {
                    var incMethod = queryExp.Type.GetMethod("Include");
                    if (incMethod == null) throw new Exception(CoreStrings.RunTimeError_Reflection_IncludeMany.Replace("IncludeMany", "Include"));
                    queryExp = Expression.Call(queryExp, incMethod.MakeGenericMethod(tbref.RefEntityType),
                        Expression.Lambda(typeof(Func<,>).MakeGenericType(entityType, tbref.RefEntityType), exp, navigateParameterExp));
                }
                void LocalIncludeMany(TableRef tbref, Expression exp, bool isthen)
                {
                    var funcType = typeof(Func<,>).MakeGenericType(entityType, typeof(IEnumerable<>).MakeGenericType(tbref.RefEntityType));
                    var navigateSelector = Expression.Lambda(funcType, exp, navigateParameterExp);
                    var incMethod = queryExp.Type.GetMethod("IncludeMany");
                    if (incMethod == null) throw new Exception(CoreStrings.RunTimeError_Reflection_IncludeMany);
                    LambdaExpression navigateThen = null;
                    var navigateThenType = typeof(Action<>).MakeGenericType(typeof(ISelect<>).MakeGenericType(tbref.RefEntityType));
                    var thenParameter = Expression.Parameter(typeof(ISelect<>).MakeGenericType(tbref.RefEntityType), "then");
                    Expression paramQueryExp = thenParameter;
                    var paramNavigateParameterExp = Expression.Parameter(tbref.RefEntityType, string.Concat((char)((byte)'a' + (depth - 1))));
                    if (isthen) paramQueryExp = LocalGetAutoIncludeQuery(paramQueryExp, depth + 1, tbref.RefEntityType, paramNavigateParameterExp, paramNavigateParameterExp, ignores);
                    navigateThen = Expression.Lambda(navigateThenType, paramQueryExp, thenParameter);
                    queryExp = Expression.Call(queryExp, incMethod.MakeGenericMethod(tbref.RefEntityType), navigateSelector, navigateThen);
                }
            }
        }
        public static string GetAutoIncludeQueryStaicCode(IFreeSql fsql, Type rootEntityType)
        {
            return $"//fsql.Select<{rootEntityType.Name}>()\r\nSelectDiy{LocalGetAutoIncludeQueryStaicCode(1, rootEntityType, "", new Stack<Type>())}";
            string LocalGetAutoIncludeQueryStaicCode(int depth, Type entityType, string navigatePath, Stack<Type> ignores)
            {
                var code = new StringBuilder();
                if (ignores.Any(a => a == entityType)) return null;
                ignores.Push(entityType);
                var table = fsql.CodeFirst.GetTableByEntity(entityType);
                if (table == null) return null;
                if (!string.IsNullOrWhiteSpace(navigatePath)) navigatePath = $"{navigatePath}.";
                foreach (var tr in table.GetAllTableRef().OrderBy(a => a.Value.RefType).ThenBy(a => a.Key))
                {
                    var tbref = tr.Value;
                    if (tbref.Exception != null) continue;
                    var navigateExpression = $"{navigatePath}{tr.Key}";
                    var depthTab = "".PadLeft(depth * 4);
                    var lambdaAlias = (char)((byte)'a' + (depth - 1));
                    var lambdaStr = $"{lambdaAlias} => {lambdaAlias}.";
                    switch (tbref.RefType)
                    {
                        case TableRefType.OneToOne:
                            if (ignores.Any(a => a == tbref.RefEntityType)) break;
                            code.Append("\r\n").Append(depthTab).Append(".Include(").Append(lambdaStr).Append(navigateExpression).Append(")");
                            code.Append(LocalGetAutoIncludeQueryStaicCode(depth, tbref.RefEntityType, navigateExpression, ignores));
                            break;
                        case TableRefType.OneToMany:
                            code.Append("\r\n").Append(depthTab).Append(".IncludeMany(").Append(lambdaStr).Append(navigateExpression);
                            var thencode = LocalGetAutoIncludeQueryStaicCode(depth + 1, tbref.RefEntityType, "", new Stack<Type>(ignores.ToArray()));
                            if (thencode.Length > 0) code.Append(", then => then").Append(thencode);
                            code.Append(")");
                            break;
                        case TableRefType.ManyToMany:
                            code.Append("\r\n").Append(depthTab).Append(".IncludeMany(").Append(lambdaStr).Append(navigateExpression).Append(")");
                            break;
                        case TableRefType.PgArrayToMany:
                            code.Append("\r\n//").Append(depthTab).Append(".IncludeMany(").Append(lambdaStr).Append(navigateExpression).Append(")");
                            break;
                        case TableRefType.ManyToOne: //不属于聚合根
                            code.Append("\r\n//").Append(depthTab).Append(".Include(").Append(lambdaStr).Append(navigateExpression).Append(")");
                            break;
                    }
                }
                ignores.Pop();
                return code.ToString();
            }
        }

        public static List<object> GetManyToManyObjects(IFreeSql fsql, TableInfo table, TableRef tbref, object entity, PropertyInfo prop)
        {
            if (tbref.RefType != TableRefType.ManyToMany) return null;
            var rights = table.GetPropertyValue(entity, prop.Name) as IEnumerable;
            if (rights == null) return null;
            var middles = new List<object>();
            var leftpkvals = new object[tbref.Columns.Count];
            for (var x = 0; x < tbref.Columns.Count; x++)
                leftpkvals[x] = Utils.GetDataReaderValue(tbref.MiddleColumns[x].CsType, EntityUtilExtensions.GetEntityValueWithPropertyName(fsql, table.Type, entity, tbref.Columns[x].CsName));
            foreach (var right in rights)
            {
                var midval = tbref.RefMiddleEntityType.CreateInstanceGetDefaultValue();
                for (var x = 0; x < tbref.Columns.Count; x++)
                    EntityUtilExtensions.SetEntityValueWithPropertyName(fsql, tbref.RefMiddleEntityType, midval, tbref.MiddleColumns[x].CsName, leftpkvals[x]);

                for (var x = tbref.Columns.Count; x < tbref.MiddleColumns.Count; x++)
                {
                    var refcol = tbref.RefColumns[x - tbref.Columns.Count];
                    var refval = EntityUtilExtensions.GetEntityValueWithPropertyName(fsql, tbref.RefEntityType, right, refcol.CsName);
                    if (refval == refcol.CsType.CreateInstanceGetDefaultValue()) throw new Exception($"ManyToMany 关联对象的主键属性({tbref.RefEntityType.DisplayCsharp()}.{refcol.CsName})不能为空");
                    refval = Utils.GetDataReaderValue(tbref.MiddleColumns[x].CsType, refval);
                    EntityUtilExtensions.SetEntityValueWithPropertyName(fsql, tbref.RefMiddleEntityType, midval, tbref.MiddleColumns[x].CsName, refval);
                }
                middles.Add(midval);
            }
            return middles;
        }
        public static void SetNavigateRelationshipValue(IFreeSql orm, TableRef tbref, Type leftType, object leftItem, object rightItem)
        {
            if (rightItem == null) return;
            switch (tbref.RefType)
            {
                case TableRefType.OneToOne:
                    for (var idx = 0; idx < tbref.Columns.Count; idx++)
                    {
                        var colval = Utils.GetDataReaderValue(tbref.RefColumns[idx].CsType, EntityUtilExtensions.GetEntityValueWithPropertyName(orm, leftType, leftItem, tbref.Columns[idx].CsName));
                        EntityUtilExtensions.SetEntityValueWithPropertyName(orm, tbref.RefEntityType, rightItem, tbref.RefColumns[idx].CsName, colval);
                    }
                    break;
                case TableRefType.OneToMany:
                    var rightEachOtm = rightItem as IEnumerable;
                    if (rightEachOtm == null) break;
                    var leftColValsOtm = new object[tbref.Columns.Count];
                    for (var idx = 0; idx < tbref.Columns.Count; idx++)
                        leftColValsOtm[idx] = Utils.GetDataReaderValue(tbref.RefColumns[idx].CsType, EntityUtilExtensions.GetEntityValueWithPropertyName(orm, leftType, leftItem, tbref.Columns[idx].CsName));
                    foreach (var rightEle in rightEachOtm)
                        for (var idx = 0; idx < tbref.Columns.Count; idx++)
                            EntityUtilExtensions.SetEntityValueWithPropertyName(orm, tbref.RefEntityType, rightEle, tbref.RefColumns[idx].CsName, leftColValsOtm[idx]);
                    break;
                case TableRefType.ManyToOne:
                    for (var idx = 0; idx < tbref.RefColumns.Count; idx++)
                    {
                        var colval = Utils.GetDataReaderValue(tbref.Columns[idx].CsType, EntityUtilExtensions.GetEntityValueWithPropertyName(orm, tbref.RefEntityType, rightItem, tbref.RefColumns[idx].CsName));
                        EntityUtilExtensions.SetEntityValueWithPropertyName(orm, leftType, leftItem, tbref.Columns[idx].CsName, colval);
                    }
                    break;
            }
        }
    }
}