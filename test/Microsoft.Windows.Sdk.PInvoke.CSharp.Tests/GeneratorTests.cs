﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Testing;
using Microsoft.Windows.Sdk.PInvoke.CSharp;
using Xunit;
using Xunit.Abstractions;

public class GeneratorTests : IDisposable, IAsyncLifetime
{
    private static readonly string FileSeparator = new string('=', 140);
    private readonly ITestOutputHelper logger;
    private CSharpCompilation compilation;
    private CSharpParseOptions parseOptions;
    private Generator? generator;

    public GeneratorTests(ITestOutputHelper logger)
    {
        this.logger = logger;

        this.parseOptions = CSharpParseOptions.Default
            .WithDocumentationMode(DocumentationMode.Diagnose)
            .WithLanguageVersion(LanguageVersion.CSharp9);
        this.compilation = null!; // set in InitializeAsync
    }

    public async Task InitializeAsync()
    {
        ReferenceAssemblies references = ReferenceAssemblies.NetStandard.NetStandard20
            .AddPackages(ImmutableArray.Create(
                new PackageIdentity("System.Memory", "4.5.4")));
        ImmutableArray<MetadataReference> metadataReferences = await references.ResolveAsync(LanguageNames.CSharp, default);

        // CONSIDER: How can I pass in the source generator itself, with AdditionalFiles, so I'm exercising that code too?
        this.compilation = CSharpCompilation.Create(
            assemblyName: "test",
            references: metadataReferences,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        this.generator?.Dispose();
    }

    [Fact]
    public void SimplestMethod()
    {
        this.generator = new Generator(compilation: this.compilation, parseOptions: this.parseOptions);
        Assert.True(this.generator.TryGenerateExternMethod("GetTickCount"));
        this.CollectGeneratedCode(this.generator);
        this.AssertNoDiagnostics();
    }

    [Theory]
    [InlineData("CreateFile")] // SafeHandle-derived type
    [InlineData("D3DGetTraceInstructionOffsets")] // SizeParamIndex
    [InlineData("PlgBlt")] // SizeConst
    [InlineData("ID3D12Resource")] // COM interface with base types
    [InlineData("ENABLE_TRACE_PARAMETERS_V1")] // bad xml created at some point.
    [InlineData("JsRuntimeVersion")] // An enum that has an extra member in a separate header file.
    [InlineData("ReportEvent")] // Failed at one point
    [InlineData("ARM64EC_NT_CONTEXT")] // Member names with type names colliding with containing type
    public void InterestingAPIs(string api)
    {
        this.generator = new Generator(compilation: this.compilation, parseOptions: this.parseOptions);
        Assert.True(this.generator.TryGenerate(api, CancellationToken.None));
        this.CollectGeneratedCode(this.generator);
        this.AssertNoDiagnostics();
    }

    [Fact]
    public void FullGeneration()
    {
        this.generator = new Generator(compilation: this.compilation, parseOptions: this.parseOptions);
        this.generator.GenerateAll(CancellationToken.None);
        this.CollectGeneratedCode(this.generator);
        this.AssertNoDiagnostics(logGeneratedCode: false);
    }

    private static ImmutableArray<Diagnostic> FilterDiagnostics(ImmutableArray<Diagnostic> diagnostics) => diagnostics.Where(d => d.Severity > DiagnosticSeverity.Hidden).ToImmutableArray();

    private void CollectGeneratedCode(Generator generator)
    {
        var compilationUnits = generator.GetCompilationUnits(CancellationToken.None);
        var syntaxTrees = new List<SyntaxTree>(compilationUnits.Count);
        foreach (var unit in compilationUnits)
        {
            // Our syntax trees aren't quite right. And anyway the source generator API only takes text anyway so it doesn't _really_ matter.
            // So render the trees as text and have C# re-parse them so we get the same compiler warnings/errors that the user would get.
            syntaxTrees.Add(CSharpSyntaxTree.ParseText(unit.Value.ToFullString(), this.parseOptions, path: unit.Key));
        }

        this.compilation = this.compilation.AddSyntaxTrees(syntaxTrees);
    }

    private void AssertNoDiagnostics(bool logGeneratedCode = true)
    {
        var diagnostics = FilterDiagnostics(this.compilation.GetDiagnostics());
        this.LogDiagnostics(diagnostics);

        var emitDiagnostics = ImmutableArray<Diagnostic>.Empty;
        bool? emitSuccessful = null;
        if (diagnostics.IsEmpty)
        {
            var emitResult = this.compilation.Emit(peStream: Stream.Null, xmlDocumentationStream: Stream.Null);
            emitSuccessful = emitResult.Success;
            emitDiagnostics = FilterDiagnostics(emitResult.Diagnostics);
            this.LogDiagnostics(emitDiagnostics);
        }

        if (logGeneratedCode)
        {
            this.LogGeneratedCode();
        }

        Assert.Empty(diagnostics);
        if (emitSuccessful.HasValue)
        {
            Assert.Empty(emitDiagnostics);
            Assert.True(emitSuccessful);
        }
    }

    private void LogDiagnostics(ImmutableArray<Diagnostic> diagnostics)
    {
        foreach (var diagnostic in diagnostics)
        {
            this.logger.WriteLine(diagnostic.ToString());
        }
    }

    private void LogGeneratedCode()
    {
        foreach (SyntaxTree tree in this.compilation.SyntaxTrees)
        {
            this.logger.WriteLine(FileSeparator);
            this.logger.WriteLine($"{tree.FilePath} content:");
            this.logger.WriteLine(FileSeparator);
            using var lineWriter = new NumberedLineWriter(this.logger);
            tree.GetRoot().WriteTo(lineWriter);
        }
    }
}