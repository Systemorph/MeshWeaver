﻿using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace MeshWeaver.CSharp.Roslyn
{
    public interface ICSharpSyntaxTreeService
    {
        SyntaxTree Parse(string code);

        CSharpCompilation GetCompilation(SyntaxTree syntaxTree, CSharpScriptOptions scriptOptions, Compilation previousScriptCompilation, Type returnType = null);
    }
}