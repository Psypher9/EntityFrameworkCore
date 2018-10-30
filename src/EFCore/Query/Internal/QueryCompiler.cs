// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Internal;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Utilities;
using Remotion.Linq.Clauses.StreamedData;

namespace Microsoft.EntityFrameworkCore.Query.Internal
{
    /// <summary>
    ///     This API supports the Entity Framework Core infrastructure and is not intended to be used
    ///     directly from your code. This API may change or be removed in future releases.
    /// </summary>
    public class QueryCompiler : IQueryCompiler
    {
        private static MethodInfo CompileQueryMethod { get; }
            = typeof(IDatabase).GetTypeInfo()
                .GetDeclaredMethod(nameof(IDatabase.CompileQuery));

        private static MethodInfo CompileAsyncQueryMethod { get; }
            = typeof(IDatabase).GetTypeInfo()
                .GetDeclaredMethod(nameof(IDatabase.CompileAsyncQuery));

        private readonly IQueryContextFactory _queryContextFactory;
        private readonly ICompiledQueryCache _compiledQueryCache;
        private readonly ICompiledQueryCacheKeyGenerator _compiledQueryCacheKeyGenerator;
        private readonly IDatabase _database;
        private readonly IDiagnosticsLogger<DbLoggerCategory.Query> _logger;
        private readonly IQueryModelGenerator _queryModelGenerator;

        private readonly Type _contextType;
        private readonly EvaluatableExpressionFilterBase2 _evaluatableExpressionFilter;

        /// <summary>
        ///     This API supports the Entity Framework Core infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        public QueryCompiler(
            [NotNull] IQueryContextFactory queryContextFactory,
            [NotNull] ICompiledQueryCache compiledQueryCache,
            [NotNull] ICompiledQueryCacheKeyGenerator compiledQueryCacheKeyGenerator,
            [NotNull] IDatabase database,
            [NotNull] IDiagnosticsLogger<DbLoggerCategory.Query> logger,
            [NotNull] ICurrentDbContext currentContext,
            [NotNull] IQueryModelGenerator queryModelGenerator,
            EvaluatableExpressionFilterBase2 evaluatableExpressionFilter)
        {
            Check.NotNull(queryContextFactory, nameof(queryContextFactory));
            Check.NotNull(compiledQueryCache, nameof(compiledQueryCache));
            Check.NotNull(compiledQueryCacheKeyGenerator, nameof(compiledQueryCacheKeyGenerator));
            Check.NotNull(database, nameof(database));
            Check.NotNull(logger, nameof(logger));
            Check.NotNull(currentContext, nameof(currentContext));

            _queryContextFactory = queryContextFactory;
            _compiledQueryCache = compiledQueryCache;
            _compiledQueryCacheKeyGenerator = compiledQueryCacheKeyGenerator;
            _database = database;
            _logger = logger;
            _contextType = currentContext.Context.GetType();
            _queryModelGenerator = queryModelGenerator;
            _evaluatableExpressionFilter = evaluatableExpressionFilter;
        }

        /// <summary>
        ///     This API supports the Entity Framework Core infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        protected virtual IDatabase Database => _database;

        /// <summary>
        ///     This API supports the Entity Framework Core infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        public virtual TResult Execute<TResult>(Expression query)
        {
            Check.NotNull(query, nameof(query));

            var queryContext = _queryContextFactory.Create();

            query = ExtractParameters(query, queryContext, _logger);
            //_queryModelGenerator.ExtractParameters(_logger, query, queryContext);

            var compiledQuery
                = _compiledQueryCache
                    .GetOrAddQuery(
                        _compiledQueryCacheKeyGenerator.GenerateCacheKey(query, async: false),
                        () => CompileQueryCore<TResult>(query, _queryModelGenerator, _database, _logger, _contextType));

            return compiledQuery(queryContext);
        }

        /// <summary>
        ///     This API supports the Entity Framework Core infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        public virtual Func<QueryContext, TResult> CreateCompiledQuery<TResult>(Expression query)
        {
            Check.NotNull(query, nameof(query));

            query = _queryModelGenerator.ExtractParameters(
                _logger, query, _queryContextFactory.Create(), parameterize: false);

            return CompileQueryCore<TResult>(query, _queryModelGenerator, _database, _logger, _contextType);
        }

        private static Func<QueryContext, TResult> CompileQueryCore<TResult>(
            Expression query,
            IQueryModelGenerator queryModelGenerator,
            IDatabase database,
            IDiagnosticsLogger<DbLoggerCategory.Query> logger,
            Type contextType)
        {
            var queryModel = queryModelGenerator.ParseQuery(query);

            var resultItemType
                = (queryModel.GetOutputDataInfo()
                      as StreamedSequenceInfo)?.ResultItemType
                  ?? typeof(TResult);

            if (resultItemType == typeof(TResult))
            {
                var compiledQuery = database.CompileQuery<TResult>(queryModel);

                return qc =>
                {
                    try
                    {
                        return compiledQuery(qc).First();
                    }
                    catch (Exception exception)
                    {
                        logger.QueryIterationFailed(contextType, exception);

                        throw;
                    }
                };
            }

            try
            {
                return (Func<QueryContext, TResult>)CompileQueryMethod
                    .MakeGenericMethod(resultItemType)
                    .Invoke(database, new object[] { queryModel });
            }
            catch (TargetInvocationException e)
            {
                ExceptionDispatchInfo.Capture(e.InnerException).Throw();

                throw;
            }
        }

        /// <summary>
        ///     This API supports the Entity Framework Core infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        public virtual TResult ExecuteAsync<TResult>(Expression query)
        {
            Check.NotNull(query, nameof(query));

            var queryContext = _queryContextFactory.Create();

            query = query = ExtractParameters(query, queryContext, _logger);
            //_queryModelGenerator.ExtractParameters(_logger, query, queryContext);

            var compiledQuery
                = _compiledQueryCache
                    .GetOrAddAsyncQuery(
                        _compiledQueryCacheKeyGenerator.GenerateCacheKey(query, async: true),
                        () => CompileAsyncQueryCore<TResult>(query, _queryModelGenerator, _database));

            return compiledQuery(queryContext);
        }

        /// <summary>
        ///     This API supports the Entity Framework Core infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        public virtual Func<QueryContext, TResult> CreateCompiledAsyncQuery<TResult>(Expression query)
        {
            Check.NotNull(query, nameof(query));

            query = _queryModelGenerator.ExtractParameters(
                _logger, query, _queryContextFactory.Create(), parameterize: false);

            return CompileAsyncQueryCore<TResult>(query, _queryModelGenerator, _database);
        }

        /// <summary>
        ///     This API supports the Entity Framework Core infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        public virtual Task<TResult> ExecuteAsync<TResult>(Expression query, CancellationToken cancellationToken)
        {
            Check.NotNull(query, nameof(query));

            var queryContext = _queryContextFactory.Create();

            queryContext.CancellationToken = cancellationToken;

            query = _queryModelGenerator.ExtractParameters(_logger, query, queryContext);

            var compiledQuery
                = _compiledQueryCache
                    .GetOrAddAsyncQuery(
                        _compiledQueryCacheKeyGenerator.GenerateCacheKey(query, async: true),
                        () => CompileAsyncQueryCore<IAsyncEnumerable<TResult>>(query, _queryModelGenerator, _database));

            return ExecuteSingletonAsyncQuery(queryContext, compiledQuery, _logger, _contextType);
        }

        /// <summary>
        ///     This API supports the Entity Framework Core infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        public virtual Func<QueryContext, Task<TResult>> CreateCompiledAsyncSingletonQuery<TResult>(Expression query)
        {
            Check.NotNull(query, nameof(query));

            query = _queryModelGenerator.ExtractParameters(
                _logger, query, _queryContextFactory.Create(), parameterize: false);

            var compiledQuery = CompileAsyncQueryCore<IAsyncEnumerable<TResult>>(query, _queryModelGenerator, _database);

            return qc => ExecuteSingletonAsyncQuery(qc, compiledQuery, _logger, _contextType);
        }

        private static async Task<TResult> ExecuteSingletonAsyncQuery<TResult>(
            QueryContext queryContext,
            Func<QueryContext, IAsyncEnumerable<TResult>> compiledQuery,
            IDiagnosticsLogger<DbLoggerCategory.Query> logger,
            Type contextType)
        {
            try
            {
                var asyncEnumerable = compiledQuery(queryContext);

                using (var asyncEnumerator = asyncEnumerable.GetEnumerator())
                {
                    await asyncEnumerator.MoveNext(queryContext.CancellationToken);

                    return asyncEnumerator.Current;
                }
            }
            catch (Exception exception)
            {
                logger.QueryIterationFailed(contextType, exception);

                throw;
            }
        }

        private static Func<QueryContext, TResult> CompileAsyncQueryCore<TResult>(
            Expression query,
            IQueryModelGenerator queryModelGenerator,
            IDatabase database)
        {
            var queryModel = queryModelGenerator.ParseQuery(query);

            var resultItemType
                = (queryModel.GetOutputDataInfo()
                      as StreamedSequenceInfo)?.ResultItemType
                  ?? typeof(TResult).TryGetSequenceType();

            try
            {
                return (Func<QueryContext, TResult>)CompileAsyncQueryMethod
                   .MakeGenericMethod(resultItemType)
                   .Invoke(database, new object[] { queryModel });
            }
            catch (TargetInvocationException e)
            {
                ExceptionDispatchInfo.Capture(e.InnerException).Throw();

                throw;
            }
        }

        private Expression ExtractParameters(
            [NotNull] Expression query,
            [NotNull] IParameterValues parameterValues,
            [NotNull] IDiagnosticsLogger<DbLoggerCategory.Query> logger
            //bool parameterize = true,
            /*bool generateContextAccessors = false*/)
        {
            return new ParameterExtractingExpressionVisitor2(
                _evaluatableExpressionFilter,
                parameterValues,
                logger)
                .ExtractParameters(query);
        }
    }

    public class ParameterExtractingExpressionVisitor2 : ExpressionVisitor
    {
        private readonly IParameterValues _parameterValues;
        private readonly IDiagnosticsLogger<DbLoggerCategory.Query> _logger;
        private readonly EvaluatableExpressionFindingExpressionVisitor _evaluatableExpressionFindingExpressionVisitor;

        private readonly Dictionary<Expression, Expression> _evaluatedValues
            = new Dictionary<Expression, Expression>(ExpressionEqualityComparer.Instance);

        private IDictionary<Expression, bool> _evaluatableExpressions;
        //private readonly bool _inLambda;
        private IQueryProvider _currentQueryProvider;

        public ParameterExtractingExpressionVisitor2(
            EvaluatableExpressionFilterBase2 evaluatableExpressionFilter,
            IParameterValues parameterValues,
            IDiagnosticsLogger<DbLoggerCategory.Query> logger)
        {
            _evaluatableExpressionFindingExpressionVisitor
                = new EvaluatableExpressionFindingExpressionVisitor(evaluatableExpressionFilter);
            _parameterValues = parameterValues;
            _logger = logger;
        }

        public Expression ExtractParameters(Expression expression)
        {
            var oldEvaluatableExpressions = _evaluatableExpressions;
            _evaluatableExpressions = _evaluatableExpressionFindingExpressionVisitor.Find(expression);

            try
            {
                return Visit(expression);
            }
            finally
            {
                _evaluatableExpressions = oldEvaluatableExpressions;
                _evaluatedValues.Clear();
            }
        }

        public override Expression Visit(Expression expression)
        {
            if (expression == null)
            {
                return null;
            }

            if (_evaluatableExpressions.TryGetValue(expression, out var generateParameter)
                && !IsConvertToObjectExpression(expression))
            {
                return Evaluate(expression, generateParameter);
            }

            return base.Visit(expression);
        }

        private static bool IsConvertToObjectExpression(Expression expression)
            => expression is UnaryExpression unaryExpression
                && (unaryExpression.Type == typeof(object)
                    || unaryExpression.Type == typeof(Enum))
                && (unaryExpression.NodeType == ExpressionType.Convert
                    || unaryExpression.NodeType == ExpressionType.ConvertChecked);

        //protected override Expression VisitLambda<T>(Expression<T> node)
        //{
        //    var oldInLambda = _inLambda;

        //    _inLambda = true;

        //    try
        //    {
        //        return base.VisitLambda(node);
        //    }
        //    finally
        //    {
        //        _inLambda = oldInLambda;
        //    }
        //}

        protected override Expression VisitBinary(BinaryExpression binaryExpression)
        {
            if (!binaryExpression.IsLogicalOperation())
            {
                return base.VisitBinary(binaryExpression);
            }

            var newLeftExpression = TryGetConstantValue(binaryExpression.Left) ?? Visit(binaryExpression.Left);
            if (ShortCircuitBinaryExpression(newLeftExpression, binaryExpression.NodeType))
            {
                return newLeftExpression;
            }

            var newRightExpression = TryGetConstantValue(binaryExpression.Right) ?? Visit(binaryExpression.Right);
            if (ShortCircuitBinaryExpression(newRightExpression, binaryExpression.NodeType))
            {
                return newRightExpression;
            }

            return binaryExpression.Update(newLeftExpression, binaryExpression.Conversion, newRightExpression);
        }

        private Expression TryGetConstantValue(Expression expression)
        {
            if (_evaluatableExpressions.ContainsKey(expression))
            {
                var value = GetValue(expression, out var _);

                if (value is bool)
                {
                    return Expression.Constant(value, typeof(bool));
                }
            }

            return null;
        }

        private static bool ShortCircuitBinaryExpression(Expression expression, ExpressionType nodeType)
            => expression is ConstantExpression constantExpression
                && constantExpression.Value is bool constantValue
                && (constantValue && nodeType == ExpressionType.OrElse
                    || !constantValue && nodeType == ExpressionType.AndAlso);

        protected override Expression VisitConstant(ConstantExpression constantExpression)
        {
            if (constantExpression.Value is IDetachableContext detachableContext)
            {
                var queryProvider = ((IQueryable)constantExpression.Value).Provider;
                if (_currentQueryProvider == null)
                {
                    _currentQueryProvider = queryProvider;
                }
                else if (!ReferenceEquals(queryProvider, _currentQueryProvider)
                    && queryProvider.GetType() == _currentQueryProvider.GetType())
                {
                    throw new InvalidOperationException(CoreStrings.ErrorInvalidQueryable);
                }

                return Expression.Constant(detachableContext.DetachContext());
            }

            return base.VisitConstant(constantExpression);
        }

        private static Expression GenerateConstantExpression(object value, Type returnType)
        {
            var constantExpression = Expression.Constant(value, value?.GetType() ?? returnType);

            return constantExpression.Type != returnType
                ? Expression.Convert(constantExpression, returnType)
                : (Expression)constantExpression;
        }

        private Expression Evaluate(Expression expression, bool generateParameter)
        {
            if (_evaluatedValues.TryGetValue(expression, out var cachedValue))
            {
                return cachedValue;
            }

            var parameterValue = GetValue(expression, out var parameterName);

            if (parameterValue is IQueryable innerQueryable)
            {
                return ExtractParameters(innerQueryable.Expression);
            }

            if (parameterValue is Expression innerExpression)
            {
                return ExtractParameters(innerExpression);
            }

            if (!generateParameter)
            {
                var constantValue = GenerateConstantExpression(parameterValue, expression.Type);

                _evaluatedValues.Add(expression, constantValue);

                return constantValue;
            }

            if (parameterName == null)
            {
                parameterName = "p";
            }

            var compilerPrefixIndex
                = parameterName.LastIndexOf(">", StringComparison.Ordinal);

            if (compilerPrefixIndex != -1)
            {
                parameterName = parameterName.Substring(compilerPrefixIndex + 1);
            }

            parameterName
                = CompiledQueryCache.CompiledQueryParameterPrefix
                    + parameterName
                    + "_"
                    + _parameterValues.ParameterValues.Count;

            _parameterValues.AddParameter(parameterName, parameterValue);

            var parameter = Expression.Parameter(expression.Type, parameterName);

            _evaluatedValues.Add(expression, parameter);

            return parameter;
        }

        private object GetValue(Expression expression, out string parameterName)
        {
            parameterName = null;

            if (expression == null)
            {
                return null;
            }

            switch (expression)
            {
                case MemberExpression memberExpression:
                    var instanceValue = GetValue(memberExpression.Expression, out parameterName);
                    try
                    {
                        switch (memberExpression.Member)
                        {
                            case FieldInfo fieldInfo:
                                parameterName = (parameterName != null ? parameterName + "_" : "") + fieldInfo.Name;
                                return fieldInfo.GetValue(instanceValue);

                            case PropertyInfo propertyInfo:
                                parameterName = (parameterName != null ? parameterName + "_" : "") + propertyInfo.Name;
                                return propertyInfo.GetValue(instanceValue);
                        }
                    }
                    catch
                    {
                        // Try again when we compile the delegate
                    }
                    break;

                case ConstantExpression constantExpression:
                    return constantExpression.Value;

                case MethodCallExpression methodCallExpression:
                    parameterName = methodCallExpression.Method.Name;
                    break;

                case UnaryExpression unaryExpression
                when unaryExpression.NodeType == ExpressionType.Convert
                    || unaryExpression.NodeType == ExpressionType.ConvertChecked:
                    return GetValue(unaryExpression.Operand, out parameterName);
            }

            try
            {
                return Expression.Lambda<Func<object>>(
                        Expression.Convert(expression, typeof(object)))
                    .Compile()
                    .Invoke();
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    _logger.ShouldLogSensitiveData()
                        ? CoreStrings.ExpressionParameterizationExceptionSensitive(expression)
                        : CoreStrings.ExpressionParameterizationException,
                    exception);
            }
        }

        private class EvaluatableExpressionFindingExpressionVisitor : ExpressionVisitor
        {
            private readonly EvaluatableExpressionFilterBase2 _evaluatableExpressionFilter;
            private readonly ISet<ParameterExpression> _allowedParameters = new HashSet<ParameterExpression>();
            //private readonly ISet<MethodInfo> _forceParametrizingMethodInfos
            //    = new HashSet<MethodInfo>
            //    {
            //        typeof(Queryable).GetMethod(nameof(Queryable.Skip)),
            //        typeof(Queryable).GetMethod(nameof(Queryable.Take)),
            //        typeof(Queryable).GetMethods().Single(
            //            mi => mi.Name == nameof(Queryable.Contains) && mi.GetParameters().Length == 2)
            //    };

            private bool _evaluatable;
            private bool _containsClosure;
            private bool _inLambda;
            private IDictionary<Expression, bool> _evaluatableExpressions;

            public EvaluatableExpressionFindingExpressionVisitor(EvaluatableExpressionFilterBase2 evaluatableExpressionFilter)
            {
                _evaluatableExpressionFilter = evaluatableExpressionFilter;
            }

            public IDictionary<Expression, bool> Find(Expression expression)
            {
                _evaluatable = true;
                _containsClosure = false;
                _inLambda = false;
                _evaluatableExpressions = new Dictionary<Expression, bool>();
                _allowedParameters.Clear();

                Visit(expression);

                return _evaluatableExpressions;
            }

            public override Expression Visit(Expression expression)
            {
                if (expression == null)
                {
                    return base.Visit(expression);
                }

                var parentEvaluatable = _evaluatable;
                var parentContainsClosure = _containsClosure;

                _evaluatable = IsEvalutableNodeType(expression)
                    && _evaluatableExpressionFilter.IsEvaluatableExpression(expression);
                _containsClosure = false;

                base.Visit(expression);

                if (_evaluatable)
                {
                    _evaluatableExpressions[expression] = _containsClosure;
                }

                _evaluatable = parentEvaluatable && _evaluatable;
                _containsClosure = parentContainsClosure || _containsClosure;

                return expression;
            }

            protected override Expression VisitLambda<T>(Expression<T> lambdaExpression)
            {
                var oldInLambda = _inLambda;
                _inLambda = true;

                // Note: Don't skip visiting parameter here.
                // SelectMany does not use parameter in lambda but we should still block it from evaluating
                base.VisitLambda(lambdaExpression);

                foreach (var parameter in lambdaExpression.Parameters)
                {
                    if (_evaluatableExpressions.ContainsKey(parameter))
                    {
                        _evaluatableExpressions.Remove(parameter);
                    }
                }

                _inLambda = oldInLambda;
                return lambdaExpression;
            }

            protected override Expression VisitMemberInit(MemberInitExpression memberInitExpression)
            {
                //var oldInLambda = _inLambda;
                //_inLambda = false;

                Visit(memberInitExpression.Bindings, VisitMemberBinding);

                if (_evaluatable)
                {
                    Visit(memberInitExpression.NewExpression);
                }

                //_inLambda = oldInLambda;

                return memberInitExpression;
            }

            protected override Expression VisitListInit(ListInitExpression listInitExpression)
            {
                //var oldInLambda = _inLambda;
                //_inLambda = false;

                Visit(listInitExpression.Initializers, VisitElementInit);

                if (_evaluatable)
                {
                    Visit(listInitExpression.NewExpression);
                }

                //_inLambda = oldInLambda;

                return listInitExpression;
            }

            //protected override Expression VisitNew(NewExpression newExpression)
            //{
            //    var oldInLambda = _inLambda;

            //    _inLambda = false;

            //    try
            //    {
            //        return base.VisitNew(newExpression);
            //    }
            //    finally
            //    {
            //        _inLambda = oldInLambda;
            //    }
            //}

            protected override Expression VisitMethodCall(MethodCallExpression methodCallExpression)
            {
                Visit(methodCallExpression.Object);
                var parameterInfos = methodCallExpression.Method.GetParameters();
                for (var i = 0; i < methodCallExpression.Arguments.Count; i++)
                {
                    if (i == 1
                        && methodCallExpression.Method.DeclaringType == typeof(Enumerable)
                        && _evaluatableExpressions.ContainsKey(methodCallExpression.Arguments[0])
                        && methodCallExpression.Arguments[1] is LambdaExpression lambdaExpression)
                    {
                        foreach (var parameter in lambdaExpression.Parameters)
                        {
                            _allowedParameters.Add(parameter);
                        }
                    }

                    Visit(methodCallExpression.Arguments[i]);

                    if (_evaluatableExpressions.ContainsKey(methodCallExpression.Arguments[i]))
                    {
                        if (parameterInfos[i].GetCustomAttribute<NotParameterizedAttribute>() != null)
                        {
                            _evaluatableExpressions[methodCallExpression.Arguments[i]] = false;
                        }
                        else if (!_inLambda)
                        {
                            _evaluatableExpressions[methodCallExpression.Arguments[i]] = true;
                        }
                    }
                }

                return methodCallExpression;
            }

            protected override Expression VisitMember(MemberExpression memberExpression)
            {
                if (memberExpression.Expression == null)
                {
                    _containsClosure
                        = !(memberExpression.Member is FieldInfo fieldInfo && fieldInfo.IsInitOnly);
                }

                return base.VisitMember(memberExpression);
            }


            protected override Expression VisitParameter(ParameterExpression parameterExpression)
            {
                _evaluatable = _allowedParameters.Contains(parameterExpression);

                return base.VisitParameter(parameterExpression);
            }

            protected override Expression VisitConstant(ConstantExpression constantExpression)
            {
                _evaluatable = !(constantExpression.Value is IDetachableContext)
                                    && !(constantExpression.Value is IQueryable);
#pragma warning disable RCS1096 // Use bitwise operation instead of calling 'HasFlag'.
                _containsClosure = constantExpression.Type.Attributes.HasFlag(TypeAttributes.NestedPrivate);
#pragma warning restore RCS1096 // Use bitwise operation instead of calling 'HasFlag'.

                return base.VisitConstant(constantExpression);
            }

            private static bool IsEvalutableNodeType(Expression expression)
            {
                if (expression.NodeType == ExpressionType.Extension)
                {
                    if (!expression.CanReduce)
                    {
                        return false;
                    }

                    return IsEvalutableNodeType(expression.ReduceAndCheck());
                }

                return true;
            }
        }
    }
}
