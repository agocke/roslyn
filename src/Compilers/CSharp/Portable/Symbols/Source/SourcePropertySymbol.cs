﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable enable

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.CSharp.Symbols
{
    internal sealed class SourcePropertySymbol : SourcePropertySymbolBase
    {
        private const string DefaultIndexerName = "Item";

        internal static SourcePropertySymbol Create(SourceMemberContainerTypeSymbol containingType, Binder bodyBinder, PropertyDeclarationSyntax syntax, DiagnosticBag diagnostics)
        {
            var nameToken = syntax.Identifier;
            var location = nameToken.GetLocation();
            return new SourcePropertySymbol(containingType, bodyBinder, syntax, nameToken.ValueText, location, diagnostics);
        }

        internal static SourcePropertySymbol Create(SourceMemberContainerTypeSymbol containingType, Binder bodyBinder, IndexerDeclarationSyntax syntax, DiagnosticBag diagnostics)
        {
            var location = syntax.ThisKeyword.GetLocation();
            return new SourcePropertySymbol(containingType, bodyBinder, syntax, DefaultIndexerName, location, diagnostics);
        }

        private SourcePropertySymbol(
           SourceMemberContainerTypeSymbol containingType,
           Binder bodyBinder,
           BasePropertyDeclarationSyntax syntax,
           string name,
           Location location,
           DiagnosticBag diagnostics)
           : base(containingType, bodyBinder, syntax, syntax.Type.GetRefKind(), name, location, diagnostics)
        {
        }

        private TypeSyntax GetTypeSyntax(SyntaxNode syntax) => ((BasePropertyDeclarationSyntax)syntax).Type;

        protected override Location TypeLocation
            => GetTypeSyntax(CSharpSyntaxNode).Location;

        protected override SyntaxTokenList GetModifierTokens(SyntaxNode syntax)
            => ((BasePropertyDeclarationSyntax)syntax).Modifiers;

        protected override ArrowExpressionClauseSyntax? GetArrowExpression(SyntaxNode syntax)
            => syntax switch
            {
                PropertyDeclarationSyntax p => p.ExpressionBody,
                IndexerDeclarationSyntax i => i.ExpressionBody,
                _ => throw ExceptionUtilities.UnexpectedValue(syntax.Kind())
            };

        protected override bool HasInitializer(SyntaxNode syntax)
            => syntax is PropertyDeclarationSyntax { Initializer: { } };

        public override SyntaxList<AttributeListSyntax> AttributeDeclarationSyntaxList
            => ((BasePropertyDeclarationSyntax)CSharpSyntaxNode).AttributeLists;

        protected override void GetAccessorDeclarations(
            CSharpSyntaxNode syntaxNode,
            DiagnosticBag diagnostics,
            out bool isAutoProperty,
            out bool hasAccessorList,
            out bool accessorsHaveImplementation,
            out bool isInitOnly,
            out CSharpSyntaxNode? getSyntax,
            out CSharpSyntaxNode? setSyntax)
        {
            var syntax = (BasePropertyDeclarationSyntax)syntaxNode;
            isAutoProperty = true;
            hasAccessorList = syntax.AccessorList != null;
            getSyntax = null;
            setSyntax = null;
            isInitOnly = false;

            if (hasAccessorList)
            {
                accessorsHaveImplementation = false;
                foreach (var accessor in syntax.AccessorList!.Accessors)
                {
                    switch (accessor.Kind())
                    {
                        case SyntaxKind.GetAccessorDeclaration:
                            if (getSyntax == null)
                            {
                                getSyntax = accessor;
                            }
                            else
                            {
                                diagnostics.Add(ErrorCode.ERR_DuplicateAccessor, accessor.Keyword.GetLocation());
                            }
                            break;
                        case SyntaxKind.SetAccessorDeclaration:
                        case SyntaxKind.InitAccessorDeclaration:
                            if (setSyntax == null)
                            {
                                setSyntax = accessor;
                                if (accessor.Keyword.IsKind(SyntaxKind.InitKeyword))
                                {
                                    isInitOnly = true;
                                }
                            }
                            else
                            {
                                diagnostics.Add(ErrorCode.ERR_DuplicateAccessor, accessor.Keyword.GetLocation());
                            }
                            break;
                        case SyntaxKind.AddAccessorDeclaration:
                        case SyntaxKind.RemoveAccessorDeclaration:
                            diagnostics.Add(ErrorCode.ERR_GetOrSetExpected, accessor.Keyword.GetLocation());
                            continue;
                        case SyntaxKind.UnknownAccessorDeclaration:
                            // We don't need to report an error here as the parser will already have
                            // done that for us.
                            continue;
                        default:
                            throw ExceptionUtilities.UnexpectedValue(accessor.Kind());
                    }

                    if (accessor.Body != null || accessor.ExpressionBody != null)
                    {
                        isAutoProperty = false;
                        accessorsHaveImplementation = true;
                    }
                }
            }
            else
            {
                isAutoProperty = false;
                accessorsHaveImplementation = GetArrowExpression(syntax) is object;
            }
        }

        protected override void CheckForBlockAndExpressionBody(CSharpSyntaxNode syntax, DiagnosticBag diagnostics)
        {
            var prop = (BasePropertyDeclarationSyntax)syntax;
            CheckForBlockAndExpressionBody(
                prop.AccessorList,
                prop.GetExpressionBodySyntax(),
                prop,
                diagnostics);
        }

        protected override DeclarationModifiers MakeModifiers(
            SyntaxTokenList modifiers,
            bool isExplicitInterfaceImplementation,
            bool isIndexer,
            bool accessorsHaveImplementation,
            Location location,
            DiagnosticBag diagnostics,
            out bool modifierErrors)
        {
            bool isInterface = this.ContainingType.IsInterface;
            var defaultAccess = isInterface && !isExplicitInterfaceImplementation ? DeclarationModifiers.Public : DeclarationModifiers.Private;

            // Check that the set of modifiers is allowed
            var allowedModifiers = DeclarationModifiers.Unsafe;
            var defaultInterfaceImplementationModifiers = DeclarationModifiers.None;

            if (!isExplicitInterfaceImplementation)
            {
                allowedModifiers |= DeclarationModifiers.New |
                                    DeclarationModifiers.Sealed |
                                    DeclarationModifiers.Abstract |
                                    DeclarationModifiers.Virtual |
                                    DeclarationModifiers.AccessibilityMask;

                if (!isIndexer)
                {
                    allowedModifiers |= DeclarationModifiers.Static;
                }

                if (!isInterface)
                {
                    allowedModifiers |= DeclarationModifiers.Override;
                }
                else
                {
                    // This is needed to make sure we can detect 'public' modifier specified explicitly and
                    // check it against language version below.
                    defaultAccess = DeclarationModifiers.None;

                    defaultInterfaceImplementationModifiers |= DeclarationModifiers.Sealed |
                                                               DeclarationModifiers.Abstract |
                                                               (isIndexer ? 0 : DeclarationModifiers.Static) |
                                                               DeclarationModifiers.Virtual |
                                                               DeclarationModifiers.Extern |
                                                               DeclarationModifiers.AccessibilityMask;
                }
            }
            else if (isInterface)
            {
                Debug.Assert(isExplicitInterfaceImplementation);
                allowedModifiers |= DeclarationModifiers.Abstract;
            }

            if (ContainingType.IsStructType())
            {
                allowedModifiers |= DeclarationModifiers.ReadOnly;
            }

            allowedModifiers |= DeclarationModifiers.Extern;

            var mods = ModifierUtils.MakeAndCheckNontypeMemberModifiers(modifiers, defaultAccess, allowedModifiers, location, diagnostics, out modifierErrors);

            this.CheckUnsafeModifier(mods, diagnostics);

            ModifierUtils.ReportDefaultInterfaceImplementationModifiers(accessorsHaveImplementation, mods,
                                                                        defaultInterfaceImplementationModifiers,
                                                                        location, diagnostics);

            // Let's overwrite modifiers for interface properties with what they are supposed to be.
            // Proper errors must have been reported by now.
            if (isInterface)
            {
                mods = ModifierUtils.AdjustModifiersForAnInterfaceMember(mods, accessorsHaveImplementation, isExplicitInterfaceImplementation);
            }

            if (isIndexer)
            {
                mods |= DeclarationModifiers.Indexer;
            }

            return mods;
        }

        protected override SourcePropertyAccessorSymbol? CreateAccessorSymbol(
            bool isGet,
            CSharpSyntaxNode? syntaxOpt,
            PropertySymbol? explicitlyImplementedPropertyOpt,
            string aliasQualifierOpt,
            bool isAutoPropertyAccessor,
            bool isExplicitInterfaceImplementation,
            DiagnosticBag diagnostics)
        {
            if (syntaxOpt is null)
            {
                return null;
            }
            return SourcePropertyAccessorSymbol.CreateAccessorSymbol(
                ContainingType,
                this,
                _modifiers,
                _sourceName,
                (AccessorDeclarationSyntax)syntaxOpt,
                explicitlyImplementedPropertyOpt,
                aliasQualifierOpt,
                isAutoPropertyAccessor,
                isExplicitInterfaceImplementation,
                diagnostics);
        }

        protected override SourcePropertyAccessorSymbol CreateExpressionBodiedAccessor(
            ArrowExpressionClauseSyntax syntax,
            PropertySymbol? explicitlyImplementedPropertyOpt,
            string aliasQualifierOpt,
            bool isExplicitInterfaceImplementation,
            DiagnosticBag diagnostics)
        {
            return SourcePropertyAccessorSymbol.CreateAccessorSymbol(
                ContainingType,
                this,
                _modifiers,
                _sourceName,
                syntax,
                explicitlyImplementedPropertyOpt,
                aliasQualifierOpt,
                isExplicitInterfaceImplementation,
                diagnostics);
        }

        protected override TypeWithAnnotations ComputeType(Binder? binder, SyntaxNode syntax, DiagnosticBag diagnostics)
        {
            return ComputeType(binder, GetTypeSyntax(syntax), syntax, diagnostics);
        }

        private static ImmutableArray<ParameterSymbol> MakeParameters(
            Binder binder, SourcePropertySymbolBase owner, BaseParameterListSyntax? parameterSyntaxOpt, DiagnosticBag diagnostics, bool addRefReadOnlyModifier)
        {
            if (parameterSyntaxOpt == null)
            {
                return ImmutableArray<ParameterSymbol>.Empty;
            }

            if (parameterSyntaxOpt.Parameters.Count < 1)
            {
                diagnostics.Add(ErrorCode.ERR_IndexerNeedsParam, parameterSyntaxOpt.GetLastToken().GetLocation());
            }

            SyntaxToken arglistToken;
            var parameters = ParameterHelpers.MakeParameters(
                binder, owner, parameterSyntaxOpt, out arglistToken,
                allowRefOrOut: false,
                allowThis: false,
                addRefReadOnlyModifier: addRefReadOnlyModifier,
                diagnostics: diagnostics);

            if (arglistToken.Kind() != SyntaxKind.None)
            {
                diagnostics.Add(ErrorCode.ERR_IllegalVarArgs, arglistToken.GetLocation());
            }

            // There is a special warning for an indexer with exactly one parameter, which is optional.
            // ParameterHelpers already warns for default values on explicit interface implementations.
            if (parameters.Length == 1 && !owner.IsExplicitInterfaceImplementation)
            {
                ParameterSyntax parameterSyntax = parameterSyntaxOpt.Parameters[0];
                if (parameterSyntax.Default != null)
                {
                    SyntaxToken paramNameToken = parameterSyntax.Identifier;
                    diagnostics.Add(ErrorCode.WRN_DefaultValueForUnconsumedLocation, paramNameToken.GetLocation(), paramNameToken.ValueText);
                }
            }

            return parameters;
        }

        protected override ImmutableArray<ParameterSymbol> ComputeParameters(Binder? binder, CSharpSyntaxNode syntax, DiagnosticBag diagnostics)
        {
            binder ??= CreateBinderForTypeAndParameters();

            var parameterSyntaxOpt = GetParameterListSyntax(syntax);
            var parameters = MakeParameters(binder, this, parameterSyntaxOpt, diagnostics, addRefReadOnlyModifier: IsVirtual || IsAbstract);
            HashSet<DiagnosticInfo>? useSiteDiagnostics = null;

            foreach (ParameterSymbol param in parameters)
            {
                if (GetExplicitInterfaceSpecifier(syntax) == null && !this.IsNoMoreVisibleThan(param.Type, ref useSiteDiagnostics))
                {
                    diagnostics.Add(ErrorCode.ERR_BadVisIndexerParam, Location, this, param.Type);
                }
                else if (SetMethod is object && param.Name == ParameterSymbol.ValueParameterName)
                {
                    diagnostics.Add(ErrorCode.ERR_DuplicateGeneratedName, param.Locations.FirstOrDefault() ?? Location, param.Name);
                }
            }

            diagnostics.Add(Location, useSiteDiagnostics);
            return parameters;
        }

        protected override bool HasPointerTypeSyntactically
            => GetTypeSyntax(CSharpSyntaxNode).IsPointerType();

        protected override ExplicitInterfaceSpecifierSyntax? GetExplicitInterfaceSpecifier(SyntaxNode syntax)
            => ((BasePropertyDeclarationSyntax)syntax).ExplicitInterfaceSpecifier;

        protected override BaseParameterListSyntax? GetParameterListSyntax(CSharpSyntaxNode syntax)
            => (syntax as IndexerDeclarationSyntax)?.ParameterList;
    }
}