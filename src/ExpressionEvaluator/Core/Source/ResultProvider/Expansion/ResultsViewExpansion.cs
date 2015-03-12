// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System.Collections.ObjectModel;
using System.Diagnostics;
using Microsoft.VisualStudio.Debugger.Clr;
using Microsoft.VisualStudio.Debugger.Evaluation;
using Microsoft.VisualStudio.Debugger.Evaluation.ClrCompilation;
using Type = Microsoft.VisualStudio.Debugger.Metadata.Type;

namespace Microsoft.CodeAnalysis.ExpressionEvaluator
{
    internal sealed class ResultsViewExpansion : Expansion
    {
        private const string ResultsFormatSpecifier = "results";

        internal static ResultsViewExpansion CreateExpansion(DkmInspectionContext inspectionContext, DkmClrValue value, Formatter formatter)
        {
            var enumerableType = GetEnumerableType(value);
            if (enumerableType == null)
            {
                return null;
            }
            return CreateExpansion(inspectionContext, value, enumerableType, formatter);
        }

        internal static EvalResultDataItem CreateResultsOnlyRow(
            DkmInspectionContext inspectionContext,
            string name,
            DkmClrType declaredType,
            DkmClrValue value,
            Formatter formatter)
        {
            string errorMessage;
            if (value.IsError())
            {
                errorMessage = (string)value.HostObjectValue;
            }
            else if (value.HasExceptionThrown(parent: null))
            {
                errorMessage = value.GetExceptionMessage(name, formatter);
            }
            else
            {
                var enumerableType = GetEnumerableType(value);
                if (enumerableType != null)
                {
                    var expansion = CreateExpansion(inspectionContext, value, enumerableType, formatter);
                    if (expansion != null)
                    {
                        return expansion.CreateResultsViewRow(inspectionContext, name, declaredType.GetLmrType(), value, includeResultsFormatSpecifier: true, formatter: formatter);
                    }
                    errorMessage = Resources.ResultsViewNoSystemCore;
                }
                else
                {
                    errorMessage = Resources.ResultsViewNotEnumerable;
                }
            }

            Debug.Assert(errorMessage != null);
            return new EvalResultDataItem(name, errorMessage);
        }

        /// <summary>
        /// Generate a Results Only row if the value is a synthesized
        /// value declared as IEnumerable or IEnumerable&lt;T&gt;.
        /// </summary>
        internal static EvalResultDataItem CreateResultsOnlyRowIfSynthesizedEnumerable(
            DkmInspectionContext inspectionContext,
            string name,
            DkmClrType declaredType,
            DkmClrValue value,
            Formatter formatter)
        {
            if ((value.ValueFlags & DkmClrValueFlags.Synthetic) == 0)
            {
                return null;
            }

            // Must be declared as IEnumerable or IEnumerable<T>, not a derived type.
            var enumerableType = GetEnumerableType(value, declaredType, requireExactInterface: true);
            if (enumerableType == null)
            {
                return null;
            }

            var expansion = CreateExpansion(inspectionContext, value, enumerableType, formatter);
            if (expansion == null)
            {
                return null;
            }

            return expansion.CreateResultsViewRow(inspectionContext, name, declaredType.GetLmrType(), value, includeResultsFormatSpecifier: false, formatter: formatter);
        }

        private static DkmClrType GetEnumerableType(DkmClrValue value)
        {
            return GetEnumerableType(value, value.Type, requireExactInterface: false);
        }

        private static bool IsEnumerableCandidate(DkmClrValue value)
        {
            Debug.Assert(!value.IsError());

            if (value.IsNull)
            {
                return false;
            }

            // Do not support Results View for strings
            // or arrays. (Matches legacy EE.)
            var type = value.Type.GetLmrType();
            return !type.IsString() && !type.IsArray;
        }

        private static DkmClrType GetEnumerableType(DkmClrValue value, DkmClrType valueType, bool requireExactInterface)
        {
            if (!IsEnumerableCandidate(value))
            {
                return null;
            }

            var type = valueType.GetLmrType();
            Type enumerableType;
            if (requireExactInterface)
            {
                if (!type.IsIEnumerable() && !type.IsIEnumerableOfT())
                {
                    return null;
                }
                enumerableType = type;
            }
            else
            {
                enumerableType = type.GetIEnumerableImplementationIfAny();
                if (enumerableType == null)
                {
                    return null;
                }
            }

            return DkmClrType.Create(valueType.AppDomain, enumerableType);
        }

        private static ResultsViewExpansion CreateExpansion(DkmInspectionContext inspectionContext, DkmClrValue value, DkmClrType enumerableType, Formatter formatter)
        {
            var proxyValue = value.InstantiateResultsViewProxy(inspectionContext, enumerableType);
            // InstantiateResultsViewProxy may return null
            // (if assembly is missing for instance).
            if (proxyValue == null)
            {
                return null;
            }

            var proxyMembers = MemberExpansion.CreateExpansion(
                inspectionContext,
                proxyValue.Type.GetLmrType(),
                proxyValue,
                ExpansionFlags.None,
                TypeHelpers.IsPublic,
                formatter);
            return new ResultsViewExpansion(proxyValue, proxyMembers);
        }

        private readonly DkmClrValue _proxyValue;
        private readonly Expansion _proxyMembers;

        private ResultsViewExpansion(DkmClrValue proxyValue, Expansion proxyMembers)
        {
            Debug.Assert(proxyValue != null);
            Debug.Assert(proxyMembers != null);

            _proxyValue = proxyValue;
            _proxyMembers = proxyMembers;
        }

        internal override void GetRows(
            ResultProvider resultProvider,
            ArrayBuilder<EvalResultDataItem> rows,
            DkmInspectionContext inspectionContext,
            EvalResultDataItem parent,
            DkmClrValue value,
            int startIndex,
            int count,
            bool visitAll,
            ref int index)
        {
            if (InRange(startIndex, count, index))
            {
                rows.Add(CreateResultsViewRow(inspectionContext, parent, resultProvider.Formatter));
            }

            index++;
        }

        private EvalResultDataItem CreateResultsViewRow(DkmInspectionContext inspectionContext, EvalResultDataItem parent, Formatter formatter)
        {
            Debug.Assert(parent != null);
            var proxyType = _proxyValue.Type.GetLmrType();
            var fullName = parent.ChildFullNamePrefix;
            var childFullNamePrefix = (fullName == null) ?
                null :
                formatter.GetObjectCreationExpression(formatter.GetTypeName(proxyType, escapeKeywordIdentifiers: true), fullName);
            return new EvalResultDataItem(
                ExpansionKind.ResultsView,
                Resources.ResultsView,
                typeDeclaringMember: null,
                declaredType: proxyType,
                parent: null,
                value: _proxyValue,
                displayValue: Resources.ResultsViewValueWarning,
                expansion: _proxyMembers,
                childShouldParenthesize: false,
                fullName: fullName,
                childFullNamePrefixOpt: childFullNamePrefix,
                formatSpecifiers: Formatter.AddFormatSpecifier(parent.FormatSpecifiers, ResultsFormatSpecifier),
                category: DkmEvaluationResultCategory.Method,
                flags: DkmEvaluationResultFlags.ReadOnly,
                editableValue: null,
                inspectionContext: inspectionContext);
        }

        private EvalResultDataItem CreateResultsViewRow(
            DkmInspectionContext inspectionContext,
            string name,
            Type declaredType,
            DkmClrValue value,
            bool includeResultsFormatSpecifier,
            Formatter formatter)
        {
            var proxyType = _proxyValue.Type.GetLmrType();
            ReadOnlyCollection<string> formatSpecifiers;
            var fullName = formatter.TrimAndGetFormatSpecifiers(name, out formatSpecifiers);
            if (includeResultsFormatSpecifier)
            {
                formatSpecifiers = Formatter.AddFormatSpecifier(formatSpecifiers, ResultsFormatSpecifier);
            }
            var childFullNamePrefix = formatter.GetObjectCreationExpression(formatter.GetTypeName(proxyType, escapeKeywordIdentifiers: true), fullName);
            return new EvalResultDataItem(
                ExpansionKind.Default,
                name,
                typeDeclaringMember: null,
                declaredType: declaredType,
                parent: null,
                value: value,
                displayValue: name,
                expansion: new IndirectExpansion(_proxyValue, _proxyMembers),
                childShouldParenthesize: false,
                fullName: fullName,
                childFullNamePrefixOpt: childFullNamePrefix,
                formatSpecifiers: formatSpecifiers,
                category: DkmEvaluationResultCategory.Method,
                flags: DkmEvaluationResultFlags.ReadOnly,
                editableValue: null,
                inspectionContext: inspectionContext);
        }

        private sealed class IndirectExpansion : Expansion
        {
            private readonly DkmClrValue _proxyValue;
            private readonly Expansion _expansion;

            internal IndirectExpansion(DkmClrValue proxyValue, Expansion expansion)
            {
                _proxyValue = proxyValue;
                _expansion = expansion;
            }

            internal override void GetRows(
                ResultProvider resultProvider,
                ArrayBuilder<EvalResultDataItem> rows,
                DkmInspectionContext inspectionContext,
                EvalResultDataItem parent,
                DkmClrValue value,
                int startIndex,
                int count,
                bool visitAll,
                ref int index)
            {
                _expansion.GetRows(resultProvider, rows, inspectionContext, parent, _proxyValue, startIndex, count, visitAll, ref index);
            }
        }
    }
}
