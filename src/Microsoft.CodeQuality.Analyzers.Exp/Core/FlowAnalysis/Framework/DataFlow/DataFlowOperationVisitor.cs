﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using Analyzer.Utilities;
using Analyzer.Utilities.Extensions;
using Microsoft.CodeAnalysis.Operations.ControlFlow;
using Microsoft.CodeAnalysis.Operations.DataFlow.CopyAnalysis;
using Microsoft.CodeAnalysis.Operations.DataFlow.PointsToAnalysis;

namespace Microsoft.CodeAnalysis.Operations.DataFlow
{
    /// <summary>
    /// Operation visitor to flow the abstract dataflow analysis values across a given statement in a basic block.
    /// </summary>
    internal abstract class DataFlowOperationVisitor<TAnalysisData, TAbstractAnalysisValue> : OperationVisitor<object, TAbstractAnalysisValue>
    {
        private readonly DataFlowAnalysisResult<CopyBlockAnalysisResult, CopyAbstractValue> _copyAnalysisResultOpt;
        private readonly DataFlowAnalysisResult<PointsToBlockAnalysisResult, PointsToAbstractValue> _pointsToAnalysisResultOpt;
        private readonly ImmutableDictionary<IOperation, TAbstractAnalysisValue>.Builder _valueCacheBuilder;
        private readonly ImmutableDictionary<IOperation, PredicateValueKind>.Builder _predicateValueKindCacheBuilder;
        private readonly List<IArgumentOperation> _pendingArgumentsToReset;
        private ImmutableDictionary<IParameterSymbol, AnalysisEntity> _lazyParameterEntities;
        private ImmutableHashSet<IMethodSymbol> _lazyContractCheckMethodsForPredicateAnalysis;
        private TAnalysisData _currentAnalysisData;
        private int _recursionDepth;

        protected abstract TAbstractAnalysisValue GetAbstractDefaultValue(ITypeSymbol type);
        protected virtual TAbstractAnalysisValue GetAbstractDefaultValueForCatchVariable(ICatchClauseOperation catchClause) => ValueDomain.UnknownOrMayBeValue;
        protected abstract void SetValueForParameterOnEntry(IParameterSymbol parameter, AnalysisEntity analysisEntity);
        protected abstract void SetValueForParameterOnExit(IParameterSymbol parameter, AnalysisEntity analysisEntity);
        protected abstract void ResetCurrentAnalysisData(TAnalysisData newAnalysisDataOpt = default(TAnalysisData));
        protected bool HasPointsToAnalysisResult => _pointsToAnalysisResultOpt != null || IsPointsToAnalysis;
        protected virtual bool IsPointsToAnalysis => false;

        public AbstractValueDomain<TAbstractAnalysisValue> ValueDomain { get; }
        protected ISymbol OwningSymbol { get; }
        protected TAnalysisData CurrentAnalysisData
        {
            get => _currentAnalysisData;
            private set
            {
                Debug.Assert(value != null);
                _currentAnalysisData = value;
            }
        }
        protected BasicBlock CurrentBasicBlock { get; private set; }
        protected IOperation CurrentStatement { get; private set; }
        protected PointsToAbstractValue ThisOrMePointsToAbstractValue { get; }
        protected AnalysisEntityFactory AnalysisEntityFactory { get; }
        protected WellKnownTypeProvider WellKnownTypeProvider { get; }

        /// <summary>
        /// This boolean field determines if the caller requires an optimistic OR a pessimistic analysis for such cases.
        /// For example, invoking an instance method may likely invalidate all the instance field analysis state, i.e.
        /// reference type fields might be re-assigned to point to different objects in the called method.
        /// An optimistic points to analysis assumes that the points to values of instance fields don't change on invoking an instance method.
        /// A pessimistic points to analysis resets all the instance state and assumes the instance field might point to any object, hence has unknown state.
        /// </summary>
        /// <remarks>
        /// For dispose analysis, we want to perform an optimistic points to analysis as we assume a disposable field is not likely to be re-assigned to a separate object in helper method invocations in Dispose.
        /// For string content analysis, we want to perform a pessimistic points to analysis to be conservative and avoid missing out true violations.
        /// </remarks>
        protected bool PessimisticAnalysis { get; }

        /// <summary>
        /// Indicates if we this visitor needs to analyze predicates of conditions.
        /// </summary>
        protected bool PredicateAnalysis { get; }

        /// <summary>
        /// Indicates if we are currently analyzing predicates of conditions.
        /// </summary>
        protected bool IsCurrentlyPerformingPredicateAnalysis => NegatedCurrentAnalysisDataStack.Count > 0;

        /// <summary>
        /// PERF: Track if we are within an <see cref="IObjectOrCollectionInitializerOperation"/> or an <see cref="IAnonymousObjectCreationOperation"/>.
        /// </summary>
        protected bool IsInsideObjectInitializer { get; private set; }

        protected DataFlowOperationVisitor(
            AbstractValueDomain<TAbstractAnalysisValue> valueDomain,
            ISymbol owningSymbol,
            WellKnownTypeProvider wellKnownTypeProvider,
            bool pessimisticAnalysis,
            bool predicateAnalysis,
            DataFlowAnalysisResult<CopyBlockAnalysisResult, CopyAbstractValue> copyAnalysisResultOpt,
            DataFlowAnalysisResult<PointsToBlockAnalysisResult, PointsToAbstractValue> pointsToAnalysisResultOpt)
        {
            Debug.Assert(owningSymbol != null);
            Debug.Assert(owningSymbol.Kind == SymbolKind.Method ||
                owningSymbol.Kind == SymbolKind.Field ||
                owningSymbol.Kind == SymbolKind.Property ||
                owningSymbol.Kind == SymbolKind.Event);
            Debug.Assert(wellKnownTypeProvider != null);

            ValueDomain = valueDomain;
            OwningSymbol = owningSymbol;
            WellKnownTypeProvider = wellKnownTypeProvider;
            PessimisticAnalysis = pessimisticAnalysis;
            PredicateAnalysis = predicateAnalysis;
            _copyAnalysisResultOpt = copyAnalysisResultOpt;
            _pointsToAnalysisResultOpt = pointsToAnalysisResultOpt;
            _valueCacheBuilder = ImmutableDictionary.CreateBuilder<IOperation, TAbstractAnalysisValue>();
            _predicateValueKindCacheBuilder = ImmutableDictionary.CreateBuilder<IOperation, PredicateValueKind>();
            _pendingArgumentsToReset = new List<IArgumentOperation>();
            ThisOrMePointsToAbstractValue = GetThisOrMeInstancePointsToValue(owningSymbol);

            AnalysisEntityFactory = new AnalysisEntityFactory(
                getPointsToAbstractValueOpt: (pointsToAnalysisResultOpt != null || IsPointsToAnalysis) ?
                    GetPointsToAbstractValue :
                    (Func<IOperation, PointsToAbstractValue>)null,
                getIsInsideObjectInitializer: () => IsInsideObjectInitializer,
                containingTypeSymbol: owningSymbol.ContainingType);
            NegatedCurrentAnalysisDataStack = new Stack<TAnalysisData>();
            MergedAnalysisDataAtBreakStatementsStack = new Stack<TAnalysisData>();
            MergedAnalysisDataAtContinueStatementsStack = new Stack<TAnalysisData>();
        }

        public override int GetHashCode()
        {
            return HashUtilities.Combine(GetType().GetHashCode(),
                HashUtilities.Combine(ValueDomain.GetHashCode(),
                HashUtilities.Combine(OwningSymbol.GetHashCode(),
                HashUtilities.Combine(WellKnownTypeProvider.Compilation.GetHashCode(),
                HashUtilities.Combine(PessimisticAnalysis.GetHashCode(),
                HashUtilities.Combine(PredicateAnalysis.GetHashCode(),
                HashUtilities.Combine(_copyAnalysisResultOpt?.GetHashCode() ?? 0,
                    _pointsToAnalysisResultOpt?.GetHashCode() ?? 0)))))));
        }

        private static PointsToAbstractValue GetThisOrMeInstancePointsToValue(ISymbol owningSymbol)
        {
            if (!owningSymbol.IsStatic &&
                !owningSymbol.ContainingType.HasValueCopySemantics())
            {
                var thisOrMeLocation = AbstractLocation.CreateThisOrMeLocation(owningSymbol.ContainingType);
                return PointsToAbstractValue.Create(thisOrMeLocation, mayBeNull: false);
            }
            else
            {
                return PointsToAbstractValue.NoLocation;
            }
        }

        /// <summary>
        /// Primary method that flows analysis data through the given statement.
        /// </summary>
        public virtual TAnalysisData Flow(IOperation statement, BasicBlock block, TAnalysisData input)
        {
            CurrentStatement = statement;
            CurrentBasicBlock = block;
            CurrentAnalysisData = input;
            Visit(statement, null);

#if DEBUG
            // Ensure that we visited and cached values for all operation descendants.
            foreach (var operation in statement.DescendantsAndSelf())
            {
                // GetState will throw an InvalidOperationException if the visitor did not visit the operation or cache it's abstract value.
                var _ = GetCachedAbstractValue(operation);
            }
#endif

            return CurrentAnalysisData;
        }

        public void OnEntry(BasicBlock entryBlock, TAnalysisData input)
        {
            CurrentBasicBlock = entryBlock;
            CurrentAnalysisData = input;

            if (_lazyParameterEntities == null &&
                OwningSymbol is IMethodSymbol method &&
                method.Parameters.Length > 0)
            {
                var builder = ImmutableDictionary.CreateBuilder<IParameterSymbol, AnalysisEntity>();
                foreach (var parameter in method.Parameters)
                {
                    var result = AnalysisEntityFactory.TryCreateForSymbolDeclaration(parameter, out AnalysisEntity analysisEntity);
                    Debug.Assert(result);
                    builder.Add(parameter, analysisEntity);
                    SetValueForParameterOnEntry(parameter, analysisEntity);
                }

                _lazyParameterEntities = builder.ToImmutable();
            }
        }

        public void OnExit(BasicBlock exitBlock, TAnalysisData input)
        {
            CurrentBasicBlock = exitBlock;
            CurrentAnalysisData = input;
            if (_lazyParameterEntities != null)
            {
                foreach (var kvp in _lazyParameterEntities)
                {
                    IParameterSymbol parameter = kvp.Key;
                    AnalysisEntity analysisEntity = kvp.Value;
                    SetValueForParameterOnExit(parameter, analysisEntity);
                }
            }
        }

        private bool IsContractCheckArgument(IArgumentOperation operation)
        {
            Debug.Assert(PredicateAnalysis);

            if (WellKnownTypeProvider.Contract != null &&
                operation.Parent is IInvocationOperation invocation &&
                invocation.TargetMethod.ContainingType == WellKnownTypeProvider.Contract &&
                invocation.TargetMethod.IsStatic &&
                invocation.Arguments[0] == operation)
            {
                if (_lazyContractCheckMethodsForPredicateAnalysis == null)
                {
                    // Contract.Requires check.
                    var requiresMethods = WellKnownTypeProvider.Contract.GetMembers("Requires");
                    var assumeMethods = WellKnownTypeProvider.Contract.GetMembers("Assume");
                    var assertMethods = WellKnownTypeProvider.Contract.GetMembers("Assert");
                    var validationMethods = requiresMethods.Concat(assumeMethods).Concat(assertMethods).OfType<IMethodSymbol>().Where(m => m.IsStatic && m.ReturnsVoid && m.Parameters.Length >= 1 && (m.Parameters[0].Type.SpecialType == SpecialType.System_Boolean));
                    _lazyContractCheckMethodsForPredicateAnalysis = ImmutableHashSet.CreateRange(validationMethods);
                }

                return _lazyContractCheckMethodsForPredicateAnalysis.Contains(invocation.TargetMethod);
            }

            return false;
        }

        #region Helper methods to get or cache analysis data for visited operations.

        public ImmutableDictionary<IOperation, TAbstractAnalysisValue> GetStateMap() => _valueCacheBuilder.ToImmutable();

        public ImmutableDictionary<IOperation, PredicateValueKind> GetPredicateValueKindMap() => _predicateValueKindCacheBuilder.ToImmutable();

        public TAnalysisData GetMergedDataForUnhandledThrowOperations()
        {
            if (AnalysisDataForUnhandledThrowOperations == null)
            {
                return default(TAnalysisData);
            }

            TAnalysisData mergedData = default(TAnalysisData);
            foreach (TAnalysisData data in AnalysisDataForUnhandledThrowOperations.Values)
            {
                mergedData = mergedData != null ? MergeAnalysisData(mergedData, data) : data;
            }

            return mergedData;
        }

        public TAbstractAnalysisValue GetCachedAbstractValue(IOperation operation)
        {
            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            TAbstractAnalysisValue state;
            if (!_valueCacheBuilder.TryGetValue(operation, out state))
            {
                throw new InvalidOperationException();
            }

            return state;
        }

        protected void CacheAbstractValue(IOperation operation, TAbstractAnalysisValue value)
        {
            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            _valueCacheBuilder[operation] = value;
        }

        protected NullAbstractValue GetNullAbstractValue(IOperation operation) => GetPointsToAbstractValue(operation).NullState;

        protected virtual CopyAbstractValue GetCopyAbstractValue(IOperation operation)
        {
            if (_copyAnalysisResultOpt == null)
            {
                return CopyAbstractValue.Unknown;
            }
            else
            {
                return _copyAnalysisResultOpt[operation];
            }
        }

        protected virtual PointsToAbstractValue GetPointsToAbstractValue(IOperation operation)
        {
            if (_pointsToAnalysisResultOpt == null)
            {
                return PointsToAbstractValue.Unknown;
            }
            else
            {
                return _pointsToAnalysisResultOpt[operation];
            }
        }

        protected bool TryGetPointsToAbstractValueAtCurrentBlockEntry(AnalysisEntity analysisEntity, out PointsToAbstractValue pointsToAbstractValue)
        {
            Debug.Assert(_pointsToAnalysisResultOpt != null);
            var inputData = _pointsToAnalysisResultOpt[CurrentBasicBlock].InputData;
            return inputData.TryGetValue(analysisEntity, out pointsToAbstractValue);
        }

        protected bool TryGetPointsToAbstractValueAtCurrentBlockExit(AnalysisEntity analysisEntity, out PointsToAbstractValue pointsToAbstractValue)
        {
            Debug.Assert(_pointsToAnalysisResultOpt != null);
            var outputData = _pointsToAnalysisResultOpt[CurrentBasicBlock].OutputData;
            return outputData.TryGetValue(analysisEntity, out pointsToAbstractValue);
        }

        protected bool TryGetNullAbstractValueAtCurrentBlockEntry(AnalysisEntity analysisEntity, out NullAbstractValue nullAbstractValue)
        {
            Debug.Assert(_pointsToAnalysisResultOpt != null);
            var inputData = _pointsToAnalysisResultOpt[CurrentBasicBlock].InputData;
            if (inputData.TryGetValue(analysisEntity, out PointsToAbstractValue pointsToAbstractValue))
            {
                nullAbstractValue = pointsToAbstractValue.NullState;
                return true;
            }

            nullAbstractValue = NullAbstractValue.MaybeNull;
            return false;
        }

        protected bool TryGetMergedNullAbstractValueAtUnhandledThrowOperationsInGraph(AnalysisEntity analysisEntity, out NullAbstractValue nullAbstractValue)
        {
            Debug.Assert(_pointsToAnalysisResultOpt != null);
            var inputData = _pointsToAnalysisResultOpt.MergedStateForUnhandledThrowOperationsOpt?.InputData;
            if (inputData == null || !inputData.TryGetValue(analysisEntity, out PointsToAbstractValue pointsToAbstractValue))
            {
                nullAbstractValue = NullAbstractValue.MaybeNull;
                return false;
            }

            nullAbstractValue = pointsToAbstractValue.NullState;
            return true;
        }

        protected virtual TAbstractAnalysisValue ComputeAnalysisValueForReferenceOperation(IOperation operation, TAbstractAnalysisValue defaultValue)
        {
            return defaultValue;
        }

        protected virtual TAbstractAnalysisValue ComputeAnalysisValueForOutArgument(IArgumentOperation operation, TAbstractAnalysisValue defaultValue)
        {
            return defaultValue;
        }

        protected virtual PredicateValueKind SetValueForComparisonOperator(IBinaryOperation operation, TAnalysisData negatedCurrentAnalysisData)
        {
            Debug.Assert(PredicateAnalysis);
            Debug.Assert(operation.IsComparisonOperator());

            var isReferenceEquality = operation.OperatorMethod == null && !operation.Type.HasValueCopySemantics();
            switch (operation.OperatorKind)
            {
                case BinaryOperatorKind.Equals:
                case BinaryOperatorKind.ObjectValueEquals:
                    return SetValueForEqualsOrNotEqualsComparisonOperator(operation.LeftOperand, operation.RightOperand, negatedCurrentAnalysisData, equals: true, isReferenceEquality: isReferenceEquality);

                case BinaryOperatorKind.NotEquals:
                case BinaryOperatorKind.ObjectValueNotEquals:
                    return SetValueForEqualsOrNotEqualsComparisonOperator(operation.LeftOperand, operation.RightOperand, negatedCurrentAnalysisData, equals: false, isReferenceEquality: isReferenceEquality);

                default:
                    return PredicateValueKind.Unknown;
            }
        }

        protected virtual PredicateValueKind SetValueForEqualsOrNotEqualsComparisonOperator(IOperation leftOperand, IOperation rightOperand, TAnalysisData negatedCurrentAnalysisData, bool equals, bool isReferenceEquality)
        {
            Debug.Assert(PredicateAnalysis);
            throw new NotImplementedException();
        }

        protected bool TryInferConversion(IConversionOperation operation, out bool alwaysSucceed, out bool alwaysFail)
        {
            // For direct cast, we assume the cast will always succeed.
            alwaysSucceed = !operation.IsTryCast;
            alwaysFail = false;

            // Bail out for user defined conversions.
            if (operation.Conversion.IsUserDefined)
            {
                return true;
            }

            // Bail out if conversion does not exist (error code).
            if (!operation.Conversion.Exists)
            {
                return false;
            }

            // Bail out for throw expression conversion.
            if (operation.Operand.Kind == OperationKind.Throw)
            {
                return true;
            }

            // Analyze if cast might always succeed or fail based on points to analysis result.
            var pointsToValue = GetPointsToAbstractValue(operation.Operand);
            if (pointsToValue.Kind == PointsToAbstractValueKind.Known)
            {
                // Bail out if we have a possible null location for direct cast.
                if (!operation.IsTryCast && pointsToValue.Locations.Any(location => location.IsNull))
                {
                    return true;
                }

                // Infer if a cast will always fail.
                // We are currently bailing out if an interface or type parameter is involved.
                bool IsInterfaceOrTypeParameter(ITypeSymbol type) => type.TypeKind == TypeKind.Interface || type.TypeKind == TypeKind.TypeParameter;
                if (!IsInterfaceOrTypeParameter(operation.Type) &&
                    pointsToValue.Locations.All(location => location.IsNull ||
                        location.IsNoLocation ||
                        (!IsInterfaceOrTypeParameter(location.LocationTypeOpt) &&
                         !operation.Type.DerivesFrom(location.LocationTypeOpt) &&
                         !location.LocationTypeOpt.DerivesFrom(operation.Type))))
                {
                    if (PredicateAnalysis)
                    {
                        _predicateValueKindCacheBuilder[operation] = PredicateValueKind.AlwaysFalse;
                    }

                    // We only set the alwaysFail flag for TryCast as direct casts that are guaranteed to fail will throw an exception and subsequent code will not execute.
                    if (operation.IsTryCast)
                    {
                        alwaysFail = true;
                    }
                }
                else
                {
                    // Infer if a TryCast will always succeed.
                    if (operation.IsTryCast &&
                        pointsToValue.Locations.All(location => location.IsNoLocation || !location.IsNull && location.LocationTypeOpt.DerivesFrom(operation.Type)))
                    {
                        // TryCast which is guaranteed to succeed, and potentially can be changed to DirectCast.
                        if (PredicateAnalysis)
                        {
                            _predicateValueKindCacheBuilder[operation] = PredicateValueKind.AlwaysTrue;
                        }

                        alwaysSucceed = true;
                    }
                }

                return true;
            }

            return false;
        }

        #endregion region

        #region Helper methods to handle initialization/assignment operations
        protected abstract void SetAbstractValueForSymbolDeclaration(ISymbol symbol, IOperation initializer, TAbstractAnalysisValue initializerValue);
        protected abstract void SetAbstractValueForElementInitializer(IOperation instance, ImmutableArray<AbstractIndex> indices, ITypeSymbol elementType, IOperation initializer, TAbstractAnalysisValue value);
        protected abstract void SetAbstractValueForAssignment(IOperation target, IOperation assignedValueOperation, TAbstractAnalysisValue assignedValue);
        protected virtual void SetAbstractDefaultValueForForEachLoopControlVariable(IForEachLoopOperation operation)
            => SetAbstractValueForAssignment(operation.LoopControlVariable, assignedValueOperation: null, assignedValue: ValueDomain.UnknownOrMayBeValue);
        #endregion

        #region Helper methods for reseting/transfer instance analysis data when PointsTo analysis results are available
        /// <summary>
        /// Resets all the analysis data for all <see cref="AnalysisEntity"/> instances that share the same <see cref="AnalysisEntity.InstanceLocation"/>
        /// as the given <paramref name="analysisEntity"/>.
        /// </summary>
        /// <param name="analysisEntity"></param>
        protected abstract void ResetValueTypeInstanceAnalysisData(AnalysisEntity analysisEntity);

        /// <summary>
        /// Resets all the analysis data for all <see cref="AnalysisEntity"/> instances that share the same <see cref="AnalysisEntity.InstanceLocation"/>
        /// as the given <paramref name="pointsToAbstractValue"/>.
        /// </summary>
        /// <param name="operation"></param>
        protected abstract void ResetReferenceTypeInstanceAnalysisData(PointsToAbstractValue pointsToAbstractValue);

        private void ResetValueTypeInstanceAnalysisData(IOperation operation)
        {
            Debug.Assert(HasPointsToAnalysisResult);
            Debug.Assert(operation.Type.HasValueCopySemantics());

            if (AnalysisEntityFactory.TryCreate(operation, out AnalysisEntity analysisEntity))
            {
                if (analysisEntity.Type.HasValueCopySemantics())
                {
                    ResetValueTypeInstanceAnalysisData(analysisEntity);
                }
            }
        }

        /// <summary>
        /// Resets all the analysis data for all <see cref="AnalysisEntity"/> instances that share the same <see cref="AnalysisEntity.InstanceLocation"/>
        /// as pointed to by given reference type <paramref name="operation"/>.
        /// </summary>
        /// <param name="operation"></param>
        private void ResetReferenceTypeInstanceAnalysisData(IOperation operation)
        {
            Debug.Assert(HasPointsToAnalysisResult);
            Debug.Assert(!operation.Type.HasValueCopySemantics());

            var pointsToValue = GetPointsToAbstractValue(operation);
            if (pointsToValue.Locations.IsEmpty)
            {
                return;
            }

            ResetReferenceTypeInstanceAnalysisData(pointsToValue);
        }

        /// <summary>
        /// Reset all the instance analysis data if <see cref="HasPointsToAnalysisResult"/> is true and <see cref="PessimisticAnalysis"/> is also true.
        /// If we are using or performing points to analysis, certain operations can invalidate all the analysis data off the containing instance.
        /// </summary>
        private void ResetInstanceAnalysisData(IOperation operation)
        {
            if (operation?.Type == null || !HasPointsToAnalysisResult || !PessimisticAnalysis)
            {
                return;
            }

            if (operation.Type.HasValueCopySemantics())
            {
                ResetValueTypeInstanceAnalysisData(operation);
            }
            else
            {
                ResetReferenceTypeInstanceAnalysisData(operation);
            }
        }

        /// <summary>
        /// Reset all the instance analysis data for <see cref="AnalysisEntityFactory.ThisOrMeInstance"/> if <see cref="HasPointsToAnalysisResult"/> is true and <see cref="PessimisticAnalysis"/> is also true.
        /// If we are using or performing points to analysis, certain operations can invalidate all the analysis data off the containing instance.
        /// </summary>
        private void ResetThisOrMeInstanceAnalysisData()
        {
            if (!HasPointsToAnalysisResult || !PessimisticAnalysis)
            {
                return;
            }

            if (AnalysisEntityFactory.ThisOrMeInstance.Type.HasValueCopySemantics())
            {
                ResetValueTypeInstanceAnalysisData(AnalysisEntityFactory.ThisOrMeInstance);
            }
            else
            {
                ResetReferenceTypeInstanceAnalysisData(ThisOrMePointsToAbstractValue);
            }
        }

        /// <summary>
        /// Resets the analysis data for an object instance passed around as an <see cref="IArgumentOperation"/>.
        /// </summary>
        private void ResetInstanceAnalysisDataForArgument(IArgumentOperation operation)
        {
            // For reference types passed as arguments, 
            // reset all analysis data for the instance members as the content might change for them.
            if (HasPointsToAnalysisResult &&
                PessimisticAnalysis &&
                operation.Value.Type != null &&
                !operation.Value.Type.HasValueCopySemantics())
            {
                ResetReferenceTypeInstanceAnalysisData(operation.Value);
            }

            // Handle ref/out arguments as escapes.
            if (operation.Parameter.RefKind != RefKind.None)
            {
                var value = GetCachedAbstractValue(operation);
                if (operation.Parameter.RefKind != RefKind.Out)
                {
                    value = ValueDomain.Merge(value, GetCachedAbstractValue(operation.Value));
                }

                SetAbstractValueForAssignment(operation.Value, operation, value);
            }
        }

        #endregion

        // TODO: Remove these temporary methods once we move to compiler's CFG
        // https://github.com/dotnet/roslyn-analyzers/issues/1567
        #region Temporary methods to workaround lack of *real* CFG
        /// <summary>
        /// Analysis data for the negated condition when executing a ConditionalOr ('||') or ConditionalAnd ('&&') expression.
        /// This is needed because we are on a stub CFG in which these operations have not have been lowered.
        /// </summary>
        protected Stack<TAnalysisData> NegatedCurrentAnalysisDataStack { get; }

        protected abstract TAnalysisData MergeAnalysisData(TAnalysisData value1, TAnalysisData value2);
        protected virtual TAnalysisData MergeAnalysisDataForBackEdge(TAnalysisData forwardEdgeAnalysisData, TAnalysisData backEdgeAnalysisData)
            => MergeAnalysisData(forwardEdgeAnalysisData, backEdgeAnalysisData);
        protected abstract TAnalysisData GetClonedAnalysisData(TAnalysisData analysisData);
        protected TAnalysisData GetClonedCurrentAnalysisData() => GetClonedAnalysisData(CurrentAnalysisData);
        protected abstract bool Equals(TAnalysisData value1, TAnalysisData value2);
        protected static bool EqualsHelper<TKey, TValue>(IDictionary<TKey, TValue> dict1, IDictionary<TKey, TValue> dict2)
            => dict1.Count == dict2.Count &&
               dict1.Keys.All(key => dict2.TryGetValue(key, out TValue value2) && EqualityComparer<TValue>.Default.Equals(dict1[key], value2));

        public override TAbstractAnalysisValue VisitCoalesce(ICoalesceOperation operation, object argument)
        {
            var leftValue = Visit(operation.Value, argument);
            var rightValue = Visit(operation.WhenNull, argument);
            if (operation.WhenNull is IThrowOperation)
            {
                return leftValue;
            }

            var leftNullValue = GetNullAbstractValue(operation.Value);
            switch (leftNullValue)
            {
                case NullAbstractValue.Null:
                    return rightValue;

                case NullAbstractValue.NotNull:
                    return leftValue;

                default:
                    return ValueDomain.Merge(leftValue, rightValue);
            }
        }

        public override TAbstractAnalysisValue VisitConditionalAccess(IConditionalAccessOperation operation, object argument)
        {
            var leftValue = Visit(operation.Operation, argument);
            var whenNullValue = Visit(operation.WhenNotNull, argument);
            var leftNullValue = GetNullAbstractValue(operation.Operation);
            switch (leftNullValue)
            {
                case NullAbstractValue.Null:
                    return GetAbstractDefaultValue(operation.WhenNotNull.Type);

                case NullAbstractValue.NotNull:
                    return whenNullValue;

                default:
                    var value1 = GetAbstractDefaultValue(operation.Type);
                    return ValueDomain.Merge(value1, whenNullValue);
            }
        }

        public override TAbstractAnalysisValue VisitConditionalAccessInstance(IConditionalAccessInstanceOperation operation, object argument)
        {
            IConditionalAccessOperation conditionalAccess = operation.GetConditionalAccess();
            return GetCachedAbstractValue(conditionalAccess.Operation);
        }

        // Temporary workaround to track analysis state at CFG exit - remove once we move to the compiler CFG.
        public TAnalysisData MergedAnalysisDataAtReturnStatements { get; private set; }

        // Temporary workaround to track analysis state at break statements - remove once we move to the compiler CFG.
        private Stack<TAnalysisData> MergedAnalysisDataAtBreakStatementsStack { get; }

        // Temporary workaround to track analysis state at continue statements - remove once we move to the compiler CFG.
        private Stack<TAnalysisData> MergedAnalysisDataAtContinueStatementsStack { get; }

        public sealed override TAbstractAnalysisValue VisitReturn(IReturnOperation operation, object argument)
        {
            var value = VisitReturnCore(operation, argument);

            MergedAnalysisDataAtReturnStatements = MergedAnalysisDataAtReturnStatements == null ?
                    GetClonedCurrentAnalysisData() :
                    MergeAnalysisData(MergedAnalysisDataAtReturnStatements, CurrentAnalysisData);

            return value;
        }

        protected virtual TAbstractAnalysisValue VisitReturnCore(IReturnOperation operation, object argument)
        {
            return base.VisitReturn(operation, argument);
        }

        public sealed override TAbstractAnalysisValue VisitBranch(IBranchOperation operation, object argument)
        {
            var value = base.VisitBranch(operation, argument);
            OnBranchOperation(operation.BranchKind);
            return value;
        }

        private void OnBranchOperation(BranchKind branchKind)
        {
            Stack<TAnalysisData> mergedAnalysisDataStack = null;
            switch (branchKind)
            {
                case BranchKind.Break:
                    mergedAnalysisDataStack = MergedAnalysisDataAtBreakStatementsStack;
                    break;

                case BranchKind.Continue:
                    mergedAnalysisDataStack = MergedAnalysisDataAtContinueStatementsStack;
                    break;
            }

            if (mergedAnalysisDataStack?.Count > 0)
            {
                TAnalysisData mergedAnalysisData = mergedAnalysisDataStack.Pop();
                mergedAnalysisData = mergedAnalysisData == null ?
                    GetClonedCurrentAnalysisData() :
                    MergeAnalysisData(mergedAnalysisData, CurrentAnalysisData);
                mergedAnalysisDataStack.Push(mergedAnalysisData);
            }
        }

        // Temporary workaround to track exception analysis state - remove once we move to the compiler CFG.
        public Dictionary<IThrowOperation, TAnalysisData> AnalysisDataForUnhandledThrowOperations { get; private set; }

        protected static bool IsBlockOperationWithBranch(IOperation operation) =>
            operation is IBlockOperation blockOperation &&
            HasBranch(blockOperation.Operations);

        private bool HasNonDefaultBranchOperationState() =>
            MergedAnalysisDataAtReturnStatements != null ||
            AnalysisDataForUnhandledThrowOperations?.Count > 0 ||
            (MergedAnalysisDataAtBreakStatementsStack.Count > 0 && MergedAnalysisDataAtBreakStatementsStack.Peek() != null) ||
            (MergedAnalysisDataAtContinueStatementsStack.Count > 0 && MergedAnalysisDataAtContinueStatementsStack.Peek() != null);

        protected static bool HasBranch(ImmutableArray<IOperation> operations)
        {
            foreach (var operation in operations)
            {
                if (operation != null)
                {
                    switch (operation.Kind)
                    {
                        case OperationKind.Return:
                        case OperationKind.Throw:
                        case OperationKind.Branch:
                            return true;

                        default:
                            if (IsBlockOperationWithBranch(operation))
                            {
                                return true;
                            }

                            break;
                    }
                }
            }

            return false;
        }

        public sealed override TAbstractAnalysisValue VisitConditional(IConditionalOperation operation, object argument)
        {
            var whenFalseBranchAnalysisData = GetClonedCurrentAnalysisData();
            if (PredicateAnalysis)
            {
                NegatedCurrentAnalysisDataStack.Push(whenFalseBranchAnalysisData);
            }
            var unusedConditionValue = Visit(operation.Condition, argument);
            if (PredicateAnalysis)
            {
                whenFalseBranchAnalysisData = NegatedCurrentAnalysisDataStack.Pop();
            }
            var whenTrue = Visit(operation.WhenTrue, argument);
            var whenTrueBranchAnalysisData = CurrentAnalysisData;
            CurrentAnalysisData = whenFalseBranchAnalysisData;
            var whenFalse = Visit(operation.WhenFalse, argument);
            whenFalseBranchAnalysisData = CurrentAnalysisData;

            if (HasNonDefaultBranchOperationState())
            {
                if (IsBlockOperationWithBranch(operation.WhenTrue))
                {
                    whenTrueBranchAnalysisData = whenFalseBranchAnalysisData;
                }
                else if (IsBlockOperationWithBranch(operation.WhenFalse))
                {
                    whenFalseBranchAnalysisData = whenTrueBranchAnalysisData;
                }
            }

            if (operation.Condition.ConstantValue.HasValue &&
                operation.Condition.ConstantValue.Value is bool condition)
            {
                CurrentAnalysisData = condition ? whenTrueBranchAnalysisData : whenFalseBranchAnalysisData;
                return condition ? whenTrue : whenFalse;
            }

            CurrentAnalysisData = MergeAnalysisData(whenTrueBranchAnalysisData, whenFalseBranchAnalysisData);
            return ValueDomain.Merge(whenTrue, whenFalse);
        }

        private void OnStartLoopOperationAnalysis()
        {
            MergedAnalysisDataAtBreakStatementsStack.Push(default(TAnalysisData));
            MergedAnalysisDataAtContinueStatementsStack.Push(default(TAnalysisData));
        }

        private void MergeAnalysisDataFromContinueStatements()
        {
            TAnalysisData mergedAnalysisDataAtContinueStatements = MergedAnalysisDataAtContinueStatementsStack.Peek();
            if (mergedAnalysisDataAtContinueStatements != null)
            {
                CurrentAnalysisData = MergeAnalysisDataForBackEdge(forwardEdgeAnalysisData: mergedAnalysisDataAtContinueStatements, backEdgeAnalysisData: CurrentAnalysisData);
            }
        }

        private void OnEndLoopOperationAnalysis()
        {
            TAnalysisData mergedAnalysisDataAtBreakStatements = MergedAnalysisDataAtBreakStatementsStack.Pop();
            if (mergedAnalysisDataAtBreakStatements != null)
            {
                CurrentAnalysisData = MergeAnalysisData(CurrentAnalysisData, mergedAnalysisDataAtBreakStatements);
            }

            MergedAnalysisDataAtContinueStatementsStack.Pop();
        }

        public sealed override TAbstractAnalysisValue VisitWhileLoop(IWhileLoopOperation operation, object argument)
        {
            OnStartLoopOperationAnalysis();
            TAnalysisData beforeLoopAnalysisData = GetClonedCurrentAnalysisData();
            TAnalysisData negatedCurrentAnalysisDataAfterCondition = default(TAnalysisData);
            TAnalysisData previousIterationAnalysisData = default(TAnalysisData);

            bool VisitCondition()
            {
                MergeAnalysisDataFromContinueStatements();

                if (PredicateAnalysis)
                {
                    NegatedCurrentAnalysisDataStack.Push(GetClonedCurrentAnalysisData());
                }

                var _ = Visit(operation.Condition, argument);

                if (PredicateAnalysis)
                {
                    negatedCurrentAnalysisDataAfterCondition = NegatedCurrentAnalysisDataStack.Pop();
                    if (operation.ConditionIsUntil)
                    {
                        var savedNegatedCurrentAnalysisData = negatedCurrentAnalysisDataAfterCondition;
                        negatedCurrentAnalysisDataAfterCondition = GetClonedCurrentAnalysisData();
                        CurrentAnalysisData = GetClonedAnalysisData(savedNegatedCurrentAnalysisData);
                    }
                }

                var fixedPointReached = previousIterationAnalysisData != null && Equals(previousIterationAnalysisData, CurrentAnalysisData);
                previousIterationAnalysisData = GetClonedCurrentAnalysisData();
                return fixedPointReached;
            };

            while (true)
            {
                if (operation.ConditionIsTop)
                {
                    if (VisitCondition())
                    {
                        break;
                    }
                }
                else if (previousIterationAnalysisData != null)
                {
                    // We are going to execute the non-first loop iteration of bottom loop.
                    CurrentAnalysisData = MergeAnalysisDataForBackEdge(forwardEdgeAnalysisData: beforeLoopAnalysisData, backEdgeAnalysisData: CurrentAnalysisData);
                }

                var unusedBodyValue = Visit(operation.Body, argument);

                if (!operation.ConditionIsTop)
                {
                    if (VisitCondition())
                    {
                        break;
                    }
                }
                else
                {
                    // We are going to execute the non-first loop iteration of top loop.
                    CurrentAnalysisData = MergeAnalysisDataForBackEdge(forwardEdgeAnalysisData: beforeLoopAnalysisData, backEdgeAnalysisData: CurrentAnalysisData);
                }
            }

            if (PredicateAnalysis)
            {
                CurrentAnalysisData = negatedCurrentAnalysisDataAfterCondition;
            }

            var unusedIgnoredCondition = Visit(operation.IgnoredCondition, argument);
            OnEndLoopOperationAnalysis();
            return ValueDomain.Bottom;
        }

        public sealed override TAbstractAnalysisValue VisitForLoop(IForLoopOperation operation, object argument)
        {
            OnStartLoopOperationAnalysis();
            var unusedBeforeValue = VisitArray(operation.Before, argument);
            var beforeLoopAnalysisData = GetClonedCurrentAnalysisData();
            TAnalysisData negatedCurrentAnalysisDataAfterCondition = default(TAnalysisData);
            TAnalysisData previousIterationAnalysisData = default(TAnalysisData);
            var fixedPointReached = false;
            while (true)
            {
                if (previousIterationAnalysisData != null)
                {
                    // We are going to execute the non-first loop iteration.
                    CurrentAnalysisData = MergeAnalysisDataForBackEdge(forwardEdgeAnalysisData: beforeLoopAnalysisData, backEdgeAnalysisData: CurrentAnalysisData);
                }

                if (PredicateAnalysis)
                {
                    NegatedCurrentAnalysisDataStack.Push(GetClonedCurrentAnalysisData());
                }

                MergeAnalysisDataFromContinueStatements();
                var unusedConditionValue = Visit(operation.Condition, argument);
                if (PredicateAnalysis)
                {
                    negatedCurrentAnalysisDataAfterCondition = NegatedCurrentAnalysisDataStack.Pop();
                }

                fixedPointReached = previousIterationAnalysisData != null && Equals(previousIterationAnalysisData, CurrentAnalysisData);
                previousIterationAnalysisData = GetClonedCurrentAnalysisData();
                if (fixedPointReached)
                {
                    break;
                }

                var unusedBodyValue = Visit(operation.Body, argument);
                var unusedLoopBottomValue = VisitArray(operation.AtLoopBottom, argument);
            }

            if (PredicateAnalysis)
            {
                CurrentAnalysisData = negatedCurrentAnalysisDataAfterCondition;
            }

            OnEndLoopOperationAnalysis();
            return ValueDomain.Bottom;
        }

        public sealed override TAbstractAnalysisValue VisitForEachLoop(IForEachLoopOperation operation, object argument)
        {
            OnStartLoopOperationAnalysis();
            var unusedLoopControlVariableValue = Visit(operation.LoopControlVariable, argument);
            SetAbstractDefaultValueForForEachLoopControlVariable(operation);

            var unusedCollectionValue = Visit(operation.Collection, argument);

            var beforeLoopAnalysisData = GetClonedCurrentAnalysisData();
            TAnalysisData previousIterationAnalysisData = default(TAnalysisData);
            var fixedPointReached = false;
            while (true)
            {
                if (previousIterationAnalysisData != null)
                {
                    // We are going to execute the non-first loop iteration.
                    CurrentAnalysisData = MergeAnalysisDataForBackEdge(forwardEdgeAnalysisData: beforeLoopAnalysisData, backEdgeAnalysisData: CurrentAnalysisData);
                }

                MergeAnalysisDataFromContinueStatements();

                fixedPointReached = previousIterationAnalysisData != null && Equals(previousIterationAnalysisData, CurrentAnalysisData);
                previousIterationAnalysisData = GetClonedCurrentAnalysisData();
                if (fixedPointReached)
                {
                    break;
                }

                var unusedBodyValue = Visit(operation.Body, argument);
            }

            OnEndLoopOperationAnalysis();
            return ValueDomain.Bottom;
        }

        public sealed override TAbstractAnalysisValue VisitForToLoop(IForToLoopOperation operation, object argument)
        {
            OnStartLoopOperationAnalysis();
            var loopControlVariableValue = Visit(operation.LoopControlVariable, argument);
            var initialValue = Visit(operation.InitialValue, argument);
            SetAbstractValueForAssignment(operation.LoopControlVariable, operation.InitialValue, initialValue);

            var beforeLoopAnalysisData = GetClonedCurrentAnalysisData();
            TAnalysisData previousIterationAnalysisData = default(TAnalysisData);
            var fixedPointReached = false;

            while (true)
            {
                if (previousIterationAnalysisData != null)
                {
                    // We are going to execute the non-first loop iteration.
                    CurrentAnalysisData = MergeAnalysisDataForBackEdge(forwardEdgeAnalysisData: beforeLoopAnalysisData, backEdgeAnalysisData: CurrentAnalysisData);
                }

                MergeAnalysisDataFromContinueStatements();

                var unusedLimitValue = Visit(operation.LimitValue, argument);
                fixedPointReached = previousIterationAnalysisData != null && Equals(previousIterationAnalysisData, CurrentAnalysisData);
                previousIterationAnalysisData = GetClonedCurrentAnalysisData();
                if (fixedPointReached)
                {
                    break;
                }

                var unusedBodyValue = Visit(operation.Body, argument);
                var stepValue = Visit(operation.StepValue, argument);
                var value = ComputeValueForIncrementOrDecrementOperation(operation, stepValue);
                SetAbstractValueForAssignment(operation.LoopControlVariable, assignedValueOperation: null, assignedValue: value);
                var unusedNextVariablesValue = VisitArray(operation.NextVariables, argument);
            }

            OnEndLoopOperationAnalysis();
            return ValueDomain.Bottom;
        }

        public virtual TAbstractAnalysisValue ComputeValueForIncrementOrDecrementOperation(IForToLoopOperation operation, TAbstractAnalysisValue stepValue)
        {
            return ValueDomain.UnknownOrMayBeValue;
        }

        public sealed override TAbstractAnalysisValue VisitSwitch(ISwitchOperation operation, object argument)
        {
            MergedAnalysisDataAtBreakStatementsStack.Push(default(TAnalysisData));
            var value = Visit(operation.Value, argument);
            var perSwitchCaseAnalysisData = new Dictionary<ISwitchCaseOperation, TAnalysisData>();
            TAnalysisData beforeSwitchCasesAnalysisData = CurrentAnalysisData;
            bool hasDefaultClause = false;
            foreach (var switchCase in operation.Cases)
            {
                var switchCaseAnalysisData = GetClonedCurrentAnalysisData();
                perSwitchCaseAnalysisData.Add(switchCase, switchCaseAnalysisData);

                // Switch with Default clause.
                if (!hasDefaultClause && switchCase.Clauses.Any(clause => clause.CaseKind == CaseKind.Default))
                {
                    hasDefaultClause = true;
                }
            }

            foreach (var switchCase in operation.Cases)
            {
                CurrentAnalysisData = perSwitchCaseAnalysisData[switchCase];
                _ = Visit(switchCase, argument);

                // Workaround for VB: Implicit break at end of each switch case that does not have an explicit branch.
                if (operation.Language == LanguageNames.VisualBasic && !HasBranch(switchCase.Body))
                {
                    OnBranchOperation(BranchKind.Break);
                }
            }

            TAnalysisData mergedAnalysisDataAtBreakStatements = MergedAnalysisDataAtBreakStatementsStack.Pop();
            if (mergedAnalysisDataAtBreakStatements != null)
            {
                // Switch statement with at least one break.
                // If default case is present, set current analysis data to merge data at break statements.
                // Otherwise, merge the data at break statements with the data before switch analysis data.
                if (hasDefaultClause)
                {
                    CurrentAnalysisData = mergedAnalysisDataAtBreakStatements;
                }
                else
                {
                    CurrentAnalysisData = MergeAnalysisData(beforeSwitchCasesAnalysisData, mergedAnalysisDataAtBreakStatements);
                }
            }
            else
            {
                // Switch statement without break.
                // If default case is present, set current analysis data to before switch analysis data.
                // Otherwise, reset the current analysis data as subsequent code is dead code.
                if (hasDefaultClause)
                {
                    CurrentAnalysisData = beforeSwitchCasesAnalysisData;
                }
                else
                {
                    ResetCurrentAnalysisData();
                }
            }

            return ValueDomain.Bottom;
        }
        #endregion

        #region Visitor methods

        internal TAbstractAnalysisValue VisitArray(IEnumerable<IOperation> operations, object argument)
        {
            foreach (var operation in operations)
            {
                _ = Visit(operation, argument);
            }

            return ValueDomain.UnknownOrMayBeValue;
        }

        public override TAbstractAnalysisValue Visit(IOperation operation, object argument)
        {
            if (operation != null)
            {
                var value = VisitCore(operation, argument);
                CacheAbstractValue(operation, value);

                if (_pendingArgumentsToReset.Any(arg => arg.Parent == operation))
                {
                    var pendingArguments = _pendingArgumentsToReset.Where(arg => arg.Parent == operation).ToImmutableArray();
                    foreach (IArgumentOperation argumentOperation in pendingArguments)
                    {
                        ResetInstanceAnalysisDataForArgument(argumentOperation);
                        _pendingArgumentsToReset.Remove(argumentOperation);
                    }
                }

                return value;
            }

            return ValueDomain.UnknownOrMayBeValue;
        }

        private TAbstractAnalysisValue VisitCore(IOperation operation, object argument)
        {
            if (operation.Kind == OperationKind.None)
            {
                return DefaultVisit(operation, argument);
            }

            _recursionDepth++;
            try
            {
                StackGuard.EnsureSufficientExecutionStack(_recursionDepth);
                return operation.Accept(this, argument);
            }
            finally
            {
                _recursionDepth--;
            }
        }

        public override TAbstractAnalysisValue DefaultVisit(IOperation operation, object argument)
        {
            return VisitArray(operation.Children, argument);
        }

        public override TAbstractAnalysisValue VisitSimpleAssignment(ISimpleAssignmentOperation operation, object argument)
        {
            return VisitAssignmentOperation(operation, argument);
        }

        public override TAbstractAnalysisValue VisitCompoundAssignment(ICompoundAssignmentOperation operation, object argument)
        {
            TAbstractAnalysisValue targetValue = Visit(operation.Target, argument);
            TAbstractAnalysisValue assignedValue = Visit(operation.Value, argument);
            var value = ComputeValueForCompoundAssignment(operation, targetValue, assignedValue);
            SetAbstractValueForAssignment(operation.Target, operation.Value, value);
            return value;
        }

        public virtual TAbstractAnalysisValue ComputeValueForCompoundAssignment(ICompoundAssignmentOperation operation, TAbstractAnalysisValue targetValue, TAbstractAnalysisValue assignedValue)
        {
            return ValueDomain.UnknownOrMayBeValue;
        }

        public override TAbstractAnalysisValue VisitIncrementOrDecrement(IIncrementOrDecrementOperation operation, object argument)
        {
            TAbstractAnalysisValue targetValue = Visit(operation.Target, argument);
            var value = ComputeValueForIncrementOrDecrementOperation(operation, targetValue);
            SetAbstractValueForAssignment(operation.Target, assignedValueOperation: null, assignedValue: value);
            return value;
        }

        public virtual TAbstractAnalysisValue ComputeValueForIncrementOrDecrementOperation(IIncrementOrDecrementOperation operation, TAbstractAnalysisValue targetValue)
        {
            return ValueDomain.UnknownOrMayBeValue;
        }

        public override TAbstractAnalysisValue VisitDeconstructionAssignment(IDeconstructionAssignmentOperation operation, object argument)
        {
            return VisitAssignmentOperation(operation, argument);
        }

        protected virtual TAbstractAnalysisValue VisitAssignmentOperation(IAssignmentOperation operation, object argument)
        {
            TAbstractAnalysisValue _ = Visit(operation.Target, argument);
            TAbstractAnalysisValue assignedValue = Visit(operation.Value, argument);
            SetAbstractValueForAssignment(operation.Target, operation.Value, assignedValue);

            return assignedValue;
        }

        public override TAbstractAnalysisValue VisitMemberInitializer(IMemberInitializerOperation operation, object argument)
        {
            TAbstractAnalysisValue _ = Visit(operation.InitializedMember, argument);
            TAbstractAnalysisValue assignedValue = Visit(operation.Initializer, argument);
            SetAbstractValueForAssignment(operation.InitializedMember, operation.Initializer, assignedValue);
            return assignedValue;
        }

        public override TAbstractAnalysisValue VisitObjectOrCollectionInitializer(IObjectOrCollectionInitializerOperation operation, object argument)
        {
            var savedIsInsideObjectInitializer = IsInsideObjectInitializer;
            IsInsideObjectInitializer = true;

            // Special handling for collection initializers as we need to track indices.
            int collectionElementInitializerIndex = 0;
            foreach (var elementInitializer in operation.Initializers)
            {
                if (elementInitializer is ICollectionElementInitializerOperation collectionElementInitializer)
                {
                    var _ = Visit(elementInitializer, argument: collectionElementInitializerIndex);
                    collectionElementInitializerIndex += collectionElementInitializer.Arguments.Length;
                }
                else
                {
                    var _ = Visit(elementInitializer, argument);
                }
            }

            IsInsideObjectInitializer = savedIsInsideObjectInitializer;
            return ValueDomain.UnknownOrMayBeValue;
        }

        public override TAbstractAnalysisValue VisitCollectionElementInitializer(ICollectionElementInitializerOperation operation, object argument)
        {
            var objectCreation = operation.GetAncestor<IObjectCreationOperation>(OperationKind.ObjectCreation);
            ITypeSymbol collectionElementType = operation.AddMethod?.Parameters.FirstOrDefault()?.Type;
            if (collectionElementType != null)
            {
                var index = (int)argument;
                for (int i = 0; i < operation.Arguments.Length; i++, index++)
                {
                    var abstractIndex = AbstractIndex.Create(index);
                    IOperation elementInitializer = operation.Arguments[i];
                    TAbstractAnalysisValue argumentValue = Visit(elementInitializer, argument: null);
                    SetAbstractValueForElementInitializer(objectCreation, ImmutableArray.Create(abstractIndex), collectionElementType, elementInitializer, argumentValue);
                }
            }
            else
            {
                var _ = base.VisitCollectionElementInitializer(operation, argument: null);
            }

            return ValueDomain.UnknownOrMayBeValue;
        }

        public override TAbstractAnalysisValue VisitArrayInitializer(IArrayInitializerOperation operation, object argument)
        {
            var arrayCreation = operation.GetAncestor<IArrayCreationOperation>(OperationKind.ArrayCreation);
            var elementType = ((IArrayTypeSymbol)arrayCreation.Type).ElementType;
            for (int index = 0; index < operation.ElementValues.Length; index++)
            {
                var abstractIndex = AbstractIndex.Create(index);
                IOperation elementInitializer = operation.ElementValues[index];
                TAbstractAnalysisValue initializerValue = Visit(elementInitializer, argument);
                SetAbstractValueForElementInitializer(arrayCreation, ImmutableArray.Create(abstractIndex), elementType, elementInitializer, initializerValue);
            }

            return ValueDomain.UnknownOrMayBeValue;
        }

        public override TAbstractAnalysisValue VisitLocalReference(ILocalReferenceOperation operation, object argument)
        {
            var value = base.VisitLocalReference(operation, argument);
            return ComputeAnalysisValueForReferenceOperation(operation, value);
        }

        public override TAbstractAnalysisValue VisitParameterReference(IParameterReferenceOperation operation, object argument)
        {
            var value = base.VisitParameterReference(operation, argument);
            return ComputeAnalysisValueForReferenceOperation(operation, value);
        }

        public override TAbstractAnalysisValue VisitArrayElementReference(IArrayElementReferenceOperation operation, object argument)
        {
            var value = base.VisitArrayElementReference(operation, argument);
            return ComputeAnalysisValueForReferenceOperation(operation, value);
        }

        public override TAbstractAnalysisValue VisitDynamicMemberReference(IDynamicMemberReferenceOperation operation, object argument)
        {
            var value = base.VisitDynamicMemberReference(operation, argument);
            return ComputeAnalysisValueForReferenceOperation(operation, value);
        }

        public override TAbstractAnalysisValue VisitEventReference(IEventReferenceOperation operation, object argument)
        {
            var value = base.VisitEventReference(operation, argument);
            return ComputeAnalysisValueForReferenceOperation(operation, value);
        }

        public override TAbstractAnalysisValue VisitFieldReference(IFieldReferenceOperation operation, object argument)
        {
            var value = base.VisitFieldReference(operation, argument);
            return ComputeAnalysisValueForReferenceOperation(operation, value);
        }

        public virtual TAbstractAnalysisValue VisitMethodReferenceCore(IMethodReferenceOperation operation, object argument)
        {
            var value = base.VisitMethodReference(operation, argument);
            return ComputeAnalysisValueForReferenceOperation(operation, value);
        }

        public sealed override TAbstractAnalysisValue VisitMethodReference(IMethodReferenceOperation operation, object argument)
        {
            var value = VisitMethodReferenceCore(operation, argument);
            if (operation.IsLambdaOrLocalFunctionOrDelegateReference())
            {
                // Reference to a lambda or local function or delegate.
                // This might be passed around as an argument, which can later be invoked from other methods.

                // Currently, we are not performing flow analysis for invocations of lambda or delegate or local function.
                // Pessimistically assume that all the current state could change and reset all our current analysis data.
                // TODO: Analyze lambda and local functions and flow the values from it's exit block to CurrentAnalysisData.
                // https://github.com/dotnet/roslyn-analyzers/issues/1547
                ResetCurrentAnalysisData();
            }
            return value;
        }

        public override TAbstractAnalysisValue VisitPropertyReference(IPropertyReferenceOperation operation, object argument)
        {
            var value = base.VisitPropertyReference(operation, argument);
            return ComputeAnalysisValueForReferenceOperation(operation, value);
        }

        public override TAbstractAnalysisValue VisitDefaultValue(IDefaultValueOperation operation, object argument)
        {
            return GetAbstractDefaultValue(operation.Type);
        }

        public override TAbstractAnalysisValue VisitInterpolation(IInterpolationOperation operation, object argument)
        {
            var expressionValue = Visit(operation.Expression, argument);
            var formatValue = Visit(operation.FormatString, argument);
            var alignmentValue = Visit(operation.Alignment, argument);
            return expressionValue;
        }

        public override TAbstractAnalysisValue VisitInterpolatedStringText(IInterpolatedStringTextOperation operation, object argument)
        {
            return Visit(operation.Text, argument);
        }

        public virtual TAbstractAnalysisValue VisitArgumentCore(IArgumentOperation operation, object argument)
        {
            return Visit(operation.Value, argument);
        }

        public sealed override TAbstractAnalysisValue VisitArgument(IArgumentOperation operation, object argument)
        {
            // Is first argument of a Contract check invocation?
            var isContractCheckArgument = PredicateAnalysis && IsContractCheckArgument(operation);
            if (isContractCheckArgument)
            {
                NegatedCurrentAnalysisDataStack.Push(GetClonedCurrentAnalysisData());
            }

            var value = VisitArgumentCore(operation, argument);
            if (operation.Parameter.RefKind != RefKind.None)
            {
                value = ComputeAnalysisValueForOutArgument(operation, defaultValue: ValueDomain.UnknownOrMayBeValue);
            }

            if (isContractCheckArgument)
            {
                NegatedCurrentAnalysisDataStack.Pop();
            }

            _pendingArgumentsToReset.Add(operation);
            return value;
        }

        public override TAbstractAnalysisValue VisitConstantPattern(IConstantPatternOperation operation, object argument)
        {
            return Visit(operation.Value, argument);
        }

        public override TAbstractAnalysisValue VisitParenthesized(IParenthesizedOperation operation, object argument)
        {
            return Visit(operation.Operand, argument);
        }

        public override TAbstractAnalysisValue VisitTranslatedQuery(ITranslatedQueryOperation operation, object argument)
        {
            return Visit(operation.Operation, argument);
        }

        public override TAbstractAnalysisValue VisitConversion(IConversionOperation operation, object argument)
        {
            var operandValue = Visit(operation.Operand, argument);

            // Conservative for error code and user defined operator.
            return operation.Conversion.Exists && !operation.Conversion.IsUserDefined ? operandValue : ValueDomain.UnknownOrMayBeValue;
        }

        protected virtual TAbstractAnalysisValue VisitSymbolInitializer(ISymbolInitializerOperation operation, ISymbol initializedSymbol, object argument)
        {
            var value = Visit(operation.Value, argument);
            SetAbstractValueForSymbolDeclaration(initializedSymbol, operation.Value, value);
            return value;
        }

        private TAbstractAnalysisValue VisitSymbolInitializer(ISymbolInitializerOperation operation, IEnumerable<ISymbol> initializedSymbols, object argument)
        {
            var value = Visit(operation.Value, argument);
            foreach (var initializedSymbol in initializedSymbols)
            {
                SetAbstractValueForSymbolDeclaration(initializedSymbol, operation.Value, value);
            }

            return value;
        }

        public override TAbstractAnalysisValue VisitVariableDeclarator(IVariableDeclaratorOperation operation, object argument)
        {
            var value = base.VisitVariableDeclarator(operation, argument);

            // Handle variable declarations without initializer (IVariableInitializerOperation). 
            var initializer = operation.GetVariableInitializer();
            if (initializer == null)
            {
                value = ValueDomain.Bottom;
                SetAbstractValueForSymbolDeclaration(operation.Symbol, initializer: null, initializerValue: value);
            }

            return value;
        }

        public override TAbstractAnalysisValue VisitVariableInitializer(IVariableInitializerOperation operation, object argument)
        {
            if (operation.Parent is IVariableDeclaratorOperation declarator)
            {
                return VisitSymbolInitializer(operation, declarator.Symbol, argument);
            }
            else if (operation.Parent is IVariableDeclarationOperation declaration)
            {
                var symbols = declaration.Declarators.Select(d => d.Symbol);
                return VisitSymbolInitializer(operation, symbols, argument);
            }

            return base.VisitVariableInitializer(operation, argument);
        }

        public override TAbstractAnalysisValue VisitFieldInitializer(IFieldInitializerOperation operation, object argument)
        {
            return VisitSymbolInitializer(operation, operation.InitializedFields, argument);
        }

        public override TAbstractAnalysisValue VisitParameterInitializer(IParameterInitializerOperation operation, object argument)
        {
            return VisitSymbolInitializer(operation, operation.Parameter, argument);
        }

        public override TAbstractAnalysisValue VisitPropertyInitializer(IPropertyInitializerOperation operation, object argument)
        {
            return VisitSymbolInitializer(operation, operation.InitializedProperties, argument);
        }

        public sealed override TAbstractAnalysisValue VisitInvocation(IInvocationOperation operation, object argument)
        {
            TAbstractAnalysisValue value;
            if (operation.TargetMethod.IsLambdaOrLocalFunctionOrDelegate())
            {
                // Invocation of a lambda or local function.
                value = VisitInvocation_LambdaOrDelegateOrLocalFunction(operation, argument);
            }
            else
            {
                value = VisitInvocation_NonLambdaOrDelegateOrLocalFunction(operation, argument);

                // Predicate analysis for different equality compare methods.
                PerformPredicateAnalysis();
            }

            // Invocation might invalidate all the analysis data on the invoked instance.
            // Conservatively reset all the instance analysis data.
            ResetInstanceAnalysisData(operation.Instance);

            return value;

            void PerformPredicateAnalysis()
            {
                if (PredicateAnalysis &&
                    operation.TargetMethod.ReturnType.SpecialType == SpecialType.System_Boolean)
                {
                    IOperation leftOperand = null;
                    IOperation rightOperand = null;
                    bool isReferenceEquality = false;
                    if (operation.Arguments.Length == 2 &&
                        operation.TargetMethod.IsStaticObjectEqualsOrReferenceEquals())
                    {
                        // 1. "static bool object.ReferenceEquals(o1, o2)"
                        // 2. "static bool object.Equals(o1, o2)"
                        leftOperand = operation.Arguments[0].Value;
                        rightOperand = operation.Arguments[1].Value;
                        isReferenceEquality = operation.TargetMethod.Name == "ReferenceEquals" ||
                            (AnalysisEntityFactory.TryCreate(operation.Arguments[0].Value, out var analysisEntity) &&
                             !analysisEntity.Type.HasValueCopySemantics() &&
                             (analysisEntity.Type as INamedTypeSymbol)?.OverridesEquals() == false);
                    }
                    else
                    {
                        // 1. "bool virtual object.Equals(other)"
                        // 2. "bool override Equals(other)"
                        // 3. "bool IEquatable<T>.Equals(other)"
                        if (operation.Arguments.Length == 1 &&
                            (operation.TargetMethod.IsObjectEquals() ||
                             operation.TargetMethod.IsObjectEqualsOverride() ||
                             IsOverrideOrImplementationOfEquatableEquals(operation.TargetMethod)))
                        {
                            leftOperand = operation.Instance;
                            rightOperand = operation.Arguments[0].Value;
                            isReferenceEquality = operation.TargetMethod.IsObjectEquals();
                        }
                    }

                    if (leftOperand != null && rightOperand != null)
                    {
                        TAnalysisData savedPreviousAnalysisDataOpt = OnStartPredicateAnalysis();
                        PredicateValueKind predicateValueKind = SetValueForEqualsOrNotEqualsComparisonOperator(
                            leftOperand,
                            rightOperand,
                            negatedCurrentAnalysisData: NegatedCurrentAnalysisDataStack.Peek(),
                            equals: true,
                            isReferenceEquality: isReferenceEquality);
                        SetPredicateValueKind(operation, predicateValueKind);
                        OnEndPredicateAnalysis(savedPreviousAnalysisDataOpt);
                    }

                    bool IsOverrideOrImplementationOfEquatableEquals(IMethodSymbol methodSymbol)
                    {
                        if (WellKnownTypeProvider.GenericIEquatable == null)
                        {
                            return false;
                        }

                        foreach (var interfaceType in methodSymbol.ContainingType.AllInterfaces)
                        {
                            if (interfaceType.OriginalDefinition.Equals(WellKnownTypeProvider.GenericIEquatable))
                            {
                                var equalsMember = interfaceType.GetMembers("Equals").OfType<IMethodSymbol>().FirstOrDefault();
                                if (equalsMember != null && methodSymbol.IsOverrideOrImplementationOfInterfaceMember(equalsMember))
                                {
                                    return true;
                                }
                            }
                        }

                        return false;
                    }
                }
            }
        }

        public virtual TAbstractAnalysisValue VisitInvocation_NonLambdaOrDelegateOrLocalFunction(IInvocationOperation operation, object argument)
        {
            return base.VisitInvocation(operation, argument);
        }

        public virtual TAbstractAnalysisValue VisitInvocation_LambdaOrDelegateOrLocalFunction(IInvocationOperation operation, object argument)
        {
            var value = base.VisitInvocation(operation, argument);

            // Currently, we are not performing flow analysis for invocations of lambda or delegate or local function.
            // Pessimistically assume that all the current state could change and reset all our current analysis data.
            // TODO: Analyze lambda and local functions and flow the values from it's exit block to CurrentAnalysisData.
            // https://github.com/dotnet/roslyn-analyzers/issues/1547
            ResetCurrentAnalysisData();
            return value;
        }

        public override TAbstractAnalysisValue VisitTuple(ITupleOperation operation, object argument)
        {
            // TODO: Handle tuples.
            // https://github.com/dotnet/roslyn-analyzers/issues/1571
            // Until the above is implemented, we pessimistically reset the current state of tuple elements.
            var value = base.VisitTuple(operation, argument);
            CacheAbstractValue(operation, value);
            foreach (var element in operation.Elements)
            {
                SetAbstractValueForAssignment(element, operation, ValueDomain.UnknownOrMayBeValue);
            }
            return value;
        }

        public virtual TAbstractAnalysisValue VisitUnaryOperatorCore(IUnaryOperation operation, object argument)
        {
            return base.VisitUnaryOperator(operation, argument);
        }

        public sealed override TAbstractAnalysisValue VisitUnaryOperator(IUnaryOperation operation, object argument)
        {
            var value = VisitUnaryOperatorCore(operation, argument);

            if (IsCurrentlyPerformingPredicateAnalysis &&
                operation.OperatorKind == UnaryOperatorKind.Not)
            {
                Debug.Assert(PredicateAnalysis);
                var negatedConditionData = NegatedCurrentAnalysisDataStack.Pop();
                NegatedCurrentAnalysisDataStack.Push(GetClonedCurrentAnalysisData());
                CurrentAnalysisData = negatedConditionData;
            }

            return value;
        }

        public virtual TAbstractAnalysisValue VisitBinaryOperator_NonConditional(IBinaryOperation operation, object argument)
        {
            Debug.Assert(!operation.IsConditionalOperator());

            if (!PredicateAnalysis || !IsCurrentlyPerformingPredicateAnalysis)
            {
                return base.VisitBinaryOperator(operation, argument);
            }

            NegatedCurrentAnalysisDataStack.Pop();
            var leftValue = Visit(operation.LeftOperand, argument);
            var rightValue = Visit(operation.RightOperand, argument);
            NegatedCurrentAnalysisDataStack.Push(GetClonedCurrentAnalysisData());
            return ValueDomain.Merge(leftValue, rightValue);
        }

        private TAnalysisData OnStartPredicateAnalysis()
        {
            TAnalysisData savedPreviousAnalysisDataOpt = default(TAnalysisData);
            if (PredicateAnalysis && !IsCurrentlyPerformingPredicateAnalysis)
            {
                savedPreviousAnalysisDataOpt = GetClonedCurrentAnalysisData();

                // Topmost predicate operator not inside a conditional expression.
                NegatedCurrentAnalysisDataStack.Push(GetClonedCurrentAnalysisData());
            }

            return savedPreviousAnalysisDataOpt;
        }

        private void OnEndPredicateAnalysis(TAnalysisData savedPreviousAnalysisDataOpt)
        {
            if (!ReferenceEquals(savedPreviousAnalysisDataOpt, default(TAnalysisData)))
            {
                Debug.Assert(PredicateAnalysis);

                // Merge the CurrentAnalysisData with savedPreviousAnalysisData. 
                CurrentAnalysisData = MergeAnalysisData(savedPreviousAnalysisDataOpt, CurrentAnalysisData);
                NegatedCurrentAnalysisDataStack.Pop();
            }
        }

        private void SetPredicateValueKind(IOperation operation, PredicateValueKind predicateValueKind)
        {
            if (predicateValueKind != PredicateValueKind.Unknown ||
                _predicateValueKindCacheBuilder.ContainsKey(operation))
            {
                _predicateValueKindCacheBuilder[operation] = predicateValueKind;
            }
        }

        public sealed override TAbstractAnalysisValue VisitBinaryOperator(IBinaryOperation operation, object argument)
        {
            if (operation.IsConditionalOperator())
            {
                TAnalysisData MergeConditional(TAnalysisData leftData, TAnalysisData rightData, bool negatedSense = false)
                {
                    Debug.Assert(operation.IsConditionalOrOperator() || operation.IsConditionalAndOperator());

                    // Conditional OR - merge the left and right conditional data for true evaluation, i.e. when negatedSense = false.
                    // Conditional AND - merge the left and right conditional data for false evaluation, i.e. when negatedSense = true.
                    var needsMerge = operation.IsConditionalOrOperator() && !negatedSense ||
                        operation.IsConditionalAndOperator() && negatedSense;
                    return needsMerge ?
                        MergeAnalysisData(leftData, rightData) :
                        rightData;
                };

                TAnalysisData savedPreviousAnalysisDataOpt = OnStartPredicateAnalysis();

                TAbstractAnalysisValue leftValue = Visit(operation.LeftOperand, argument);

                TAnalysisData leftConditionalData = GetClonedCurrentAnalysisData();
                TAnalysisData leftNegatedCurrentAnalysisData = default(TAnalysisData);
                if (PredicateAnalysis)
                {
                    leftNegatedCurrentAnalysisData = GetClonedAnalysisData(NegatedCurrentAnalysisDataStack.Peek());

                    // 1. For conditional or ('||'), we execute the right operand only if left operand condition is false.
                    //    So we use the current NegatedCurrentAnalysisData as both the CurrentAnalysisData and the current NegatedCurrentAnalysisData.
                    // 2. For conditional and ('&&'), we execute the right operand only if left operand condition is true.
                    //    So we use the CurrentAnalysisData as both the CurrentAnalysisData and the current NegatedCurrentAnalysisData.
                    if (operation.IsConditionalOrOperator())
                    {
                        CurrentAnalysisData = GetClonedAnalysisData(NegatedCurrentAnalysisDataStack.Peek());
                        leftNegatedCurrentAnalysisData = GetClonedAnalysisData(NegatedCurrentAnalysisDataStack.Peek());
                    }
                    else if (operation.IsConditionalAndOperator())
                    {
                        NegatedCurrentAnalysisDataStack.Pop();
                        NegatedCurrentAnalysisDataStack.Push(GetClonedCurrentAnalysisData());
                    }
                }

                TAbstractAnalysisValue rightValue = Visit(operation.RightOperand, argument);
                var rightConditionalData = CurrentAnalysisData;

                CurrentAnalysisData = MergeConditional(leftConditionalData, rightConditionalData);

                if (PredicateAnalysis && savedPreviousAnalysisDataOpt == null)
                {
                    // Update the latest NegatedCurrentAnalysisData for parent operations. 
                    TAnalysisData rightNegatedCurrentAnalysisData = NegatedCurrentAnalysisDataStack.Pop();
                    var mergedNegatedCurrentAnalysisData = MergeConditional(leftNegatedCurrentAnalysisData, rightNegatedCurrentAnalysisData, negatedSense: true);
                    NegatedCurrentAnalysisDataStack.Push(mergedNegatedCurrentAnalysisData);
                }
                else
                {
                    OnEndPredicateAnalysis(savedPreviousAnalysisDataOpt);
                }

                return ValueDomain.UnknownOrMayBeValue;
            }

            if (PredicateAnalysis && operation.IsComparisonOperator())
            {
                TAnalysisData savedPreviousAnalysisDataOpt = OnStartPredicateAnalysis();
                var value = VisitBinaryOperator_NonConditional(operation, argument);

                PredicateValueKind predicateKind = SetValueForComparisonOperator(operation, NegatedCurrentAnalysisDataStack.Peek());
                SetPredicateValueKind(operation, predicateKind);

                OnEndPredicateAnalysis(savedPreviousAnalysisDataOpt);
                return value;
            }

            return VisitBinaryOperator_NonConditional(operation, argument);
        }

        public virtual TAbstractAnalysisValue VisitThrowCore(IThrowOperation operation, object argument)
        {
            return base.VisitThrow(operation, argument);
        }

        public sealed override TAbstractAnalysisValue VisitThrow(IThrowOperation operation, object argument)
        {
            var value = VisitThrowCore(operation, argument);
            var exceptionType = operation.GetExceptionType();
            if (exceptionType != null &&
                exceptionType.DerivesFrom(WellKnownTypeProvider.Exception, baseTypesOnly: true))
            {
                AnalysisDataForUnhandledThrowOperations = AnalysisDataForUnhandledThrowOperations ?? new Dictionary<IThrowOperation, TAnalysisData>();
                AnalysisDataForUnhandledThrowOperations[operation] = GetClonedCurrentAnalysisData();
            }

            return value;
        }

        public sealed override TAbstractAnalysisValue VisitTry(ITryOperation operation, object argument)
        {
            var bodyValue = Visit(operation.Body, argument);

            if (operation.Catches.Length > 0)
            {
                TAnalysisData previousAnalysisData = GetClonedCurrentAnalysisData();
                TAnalysisData mergedCatchClauseAnalysisData = GetClonedCurrentAnalysisData();
                foreach (ICatchClauseOperation catchClause in operation.Catches)
                {
                    // Execute from explicit throw statements within try.
                    if (AnalysisDataForUnhandledThrowOperations?.Count > 0)
                    {
                        foreach (IThrowOperation pendingThrowOperation in AnalysisDataForUnhandledThrowOperations.Keys.ToArray())
                        {
                            var pendingException = (INamedTypeSymbol)pendingThrowOperation.GetExceptionType();
                            Debug.Assert(pendingException.DerivesFrom(WellKnownTypeProvider.Exception, baseTypesOnly: true));

                            if (pendingException.DerivesFrom(catchClause.ExceptionType, baseTypesOnly: true))
                            {
                                CurrentAnalysisData = AnalysisDataForUnhandledThrowOperations[pendingThrowOperation];
                                AnalysisDataForUnhandledThrowOperations.Remove(pendingThrowOperation);
                                var unusedValue = Visit(catchClause, argument);
                                mergedCatchClauseAnalysisData = MergeAnalysisData(CurrentAnalysisData, mergedCatchClauseAnalysisData);
                            }
                        }
                    }

                    // Execute from end of try.
                    CurrentAnalysisData = GetClonedAnalysisData(previousAnalysisData);
                    var unusedCatchValue = Visit(catchClause, argument);
                    mergedCatchClauseAnalysisData = MergeAnalysisData(CurrentAnalysisData, mergedCatchClauseAnalysisData);
                }

                CurrentAnalysisData = mergedCatchClauseAnalysisData;
            }

            var _ = Visit(operation.Finally, argument);
            return ValueDomain.UnknownOrMayBeValue;
        }

        public sealed override TAbstractAnalysisValue VisitCatchClause(ICatchClauseOperation operation, object argument)
        {
            var _ = Visit(operation.ExceptionDeclarationOrExpression, argument);
            if (operation.ExceptionDeclarationOrExpression != null)
            {
                SetAbstractValueForAssignment(operation.ExceptionDeclarationOrExpression,
                    assignedValueOperation: operation, assignedValue: GetAbstractDefaultValueForCatchVariable(operation));
            }

            if (operation.Filter != null)
            {
                if (PredicateAnalysis)
                {
                    NegatedCurrentAnalysisDataStack.Push(GetClonedCurrentAnalysisData());
                }

                _ = Visit(operation.Filter, argument);

                if (PredicateAnalysis)
                {
                    NegatedCurrentAnalysisDataStack.Pop();
                }
            }

            _ = Visit(operation.Handler, argument);
            return ValueDomain.UnknownOrMayBeValue;
        }

        public override TAbstractAnalysisValue VisitLocalFunction(ILocalFunctionOperation operation, object argument)
        {
            var savedCurrentAnalysisData = GetClonedCurrentAnalysisData();
            ResetCurrentAnalysisData();
            var value = base.VisitLocalFunction(operation, argument);
            ResetCurrentAnalysisData();
            CurrentAnalysisData = savedCurrentAnalysisData;
            return value;
        }

        public override TAbstractAnalysisValue VisitAnonymousFunction(IAnonymousFunctionOperation operation, object argument)
        {
            var savedCurrentAnalysisData = GetClonedCurrentAnalysisData();
            ResetCurrentAnalysisData();
            var value = base.VisitAnonymousFunction(operation, argument);
            ResetCurrentAnalysisData();
            CurrentAnalysisData = savedCurrentAnalysisData;
            return value;
        }

        public override TAbstractAnalysisValue VisitAnonymousObjectCreation(IAnonymousObjectCreationOperation operation, object argument)
        {
            var savedIsInsideObjectInitializer = IsInsideObjectInitializer;
            IsInsideObjectInitializer = true;
            var value = base.VisitAnonymousObjectCreation(operation, argument);
            IsInsideObjectInitializer = savedIsInsideObjectInitializer;
            return value;
        }

        public override TAbstractAnalysisValue VisitLock(ILockOperation operation, object argument)
        {
            // Multi-threaded instance method.
            // Conservatively reset all the instance analysis data for the ThisOrMeInstance.
            ResetThisOrMeInstanceAnalysisData();

            return base.VisitLock(operation, argument);
        }

        #endregion
    }
}