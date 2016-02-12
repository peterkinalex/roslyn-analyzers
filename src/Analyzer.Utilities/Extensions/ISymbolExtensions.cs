﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Analyzer.Utilities
{
    public static class ISymbolExtensions
    {
        public static bool IsType(this ISymbol symbol)
        {
            var typeSymbol = symbol as ITypeSymbol;
            return typeSymbol != null && typeSymbol.IsType;
        }

        public static bool IsAccessorMethod(this ISymbol symbol)
        {
            var accessorSymbol = symbol as IMethodSymbol;
            return accessorSymbol != null &&
                (accessorSymbol.MethodKind == MethodKind.PropertySet || accessorSymbol.MethodKind == MethodKind.PropertyGet ||
                accessorSymbol.MethodKind == MethodKind.EventRemove || accessorSymbol.MethodKind == MethodKind.EventAdd);
        }

        public static bool IsPublic(this ISymbol symbol)
        {
            return symbol.DeclaredAccessibility == Accessibility.Public;
        }

        public static bool IsErrorType(this ISymbol symbol)
        {
            return
                symbol is ITypeSymbol &&
                ((ITypeSymbol)symbol).TypeKind == TypeKind.Error;
        }

        public static bool IsConstructor(this ISymbol symbol)
        {
            return (symbol as IMethodSymbol)?.MethodKind == MethodKind.Constructor;
        }

        public static bool IsDestructor(this ISymbol symbol)
        {
            return (symbol as IMethodSymbol)?.MethodKind == MethodKind.Destructor;
        }

        public static bool IsIndexer(this ISymbol symbol)
        {
            return (symbol as IPropertySymbol)?.IsIndexer == true;
        }

        public static bool IsUserDefinedOperator(this ISymbol symbol)
        {
            return (symbol as IMethodSymbol)?.MethodKind == MethodKind.UserDefinedOperator;
        }

        public static ImmutableArray<IParameterSymbol> GetParameters(this ISymbol symbol)
        {
            return symbol.TypeSwitch(
                (IMethodSymbol m) => m.Parameters,
                (IPropertySymbol p) => p.Parameters,
                _ => ImmutableArray.Create<IParameterSymbol>());
        }

        public static SymbolVisibility GetResultantVisibility(this ISymbol symbol)
        {
            // Start by assuming it's visible.
            SymbolVisibility visibility = SymbolVisibility.Public;

            switch (symbol.Kind)
            {
                case SymbolKind.Alias:
                    // Aliases are uber private.  They're only visible in the same file that they
                    // were declared in.
                    return SymbolVisibility.Private;

                case SymbolKind.Parameter:
                    // Parameters are only as visible as their containing symbol
                    return GetResultantVisibility(symbol.ContainingSymbol);

                case SymbolKind.TypeParameter:
                    // Type Parameters are private.
                    return SymbolVisibility.Private;
            }

            while (symbol != null && symbol.Kind != SymbolKind.Namespace)
            {
                switch (symbol.DeclaredAccessibility)
                {
                    // If we see anything private, then the symbol is private.
                    case Accessibility.NotApplicable:
                    case Accessibility.Private:
                        return SymbolVisibility.Private;

                    // If we see anything internal, then knock it down from public to
                    // internal.
                    case Accessibility.Internal:
                    case Accessibility.ProtectedAndInternal:
                        visibility = SymbolVisibility.Internal;
                        break;

                        // For anything else (Public, Protected, ProtectedOrInternal), the
                        // symbol stays at the level we've gotten so far.
                }

                symbol = symbol.ContainingSymbol;
            }

            return visibility;
        }

        public static bool MatchMemberDerivedByName(this ISymbol member, INamedTypeSymbol type, string name)
        {
            return member != null && member.ContainingType.DerivesFrom(type) && member.MetadataName == name;
        }

        public static bool MatchMethodDerivedByName(this ISymbol member, INamedTypeSymbol type, string name)
        {
            return member != null && member.Kind == SymbolKind.Method && member.MatchMemberDerivedByName(type, name);
        }

        public static bool MatchMethodByName(this ISymbol member, INamedTypeSymbol type, string name)
        {
            return member != null && member.Kind == SymbolKind.Method && member.MatchMemberByName(type, name);
        }

        public static bool MatchPropertyDerivedByName(this ISymbol member, INamedTypeSymbol type, string name)
        {
            return member != null && member.Kind == SymbolKind.Property && member.MatchMemberDerivedByName(type, name);
        }

        public static bool MatchFieldDerivedByName(this ISymbol member, INamedTypeSymbol type, string name)
        {
            return member != null && member.Kind == SymbolKind.Field && member.MatchMemberDerivedByName(type, name);
        }

        public static bool MatchMemberByName(this ISymbol member, INamedTypeSymbol type, string name)
        {
            return member != null && member.ContainingType == type && member.MetadataName == name;
        }

        public static bool MatchPropertyByName(this ISymbol member, INamedTypeSymbol type, string name)
        {
            return member != null && member.Kind == SymbolKind.Property && member.MatchMemberByName(type, name);
        }

        public static bool MatchFieldByName(this ISymbol member, INamedTypeSymbol type, string name)
        {
            return member != null && member.Kind == SymbolKind.Field && member.MatchMemberByName(type, name);
        }

        // Define the format in for displaying member names. The format is chosen to be consistent
        // consistent with FxCop's display format.
        private static readonly SymbolDisplayFormat s_memberDisplayFormat =
            // This format omits the namespace.
            SymbolDisplayFormat.CSharpShortErrorMessageFormat
                // Turn off the EscapeKeywordIdentifiers flag (which is on by default), so that
                // a method named "@for" is displayed as "for".
                // Turn on the UseSpecialTypes flat (which is off by default), so that parameter
                // names of "special" types such as Int32 are displayed as their language alias,
                // such as int for C# and Integer for VB.
                .WithMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

        /// <summary>
        /// Format member names in a way consistent with FxCop's display format.
        /// </summary>
        /// <param name="member"></param>
        /// <returns>
        /// A string representing the name of the member in a format consistent with FxCop.
        /// </returns>
        public static string FormatMemberName(this ISymbol member)
        {
            return member.ToDisplayString(s_memberDisplayFormat);
        }
    }

    public enum SymbolVisibility
    {
        Public,
        Internal,
        Private,
    }
}
