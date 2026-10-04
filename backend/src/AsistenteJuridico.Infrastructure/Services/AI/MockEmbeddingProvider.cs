using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using AsistenteJuridico.Application.Common.Interfaces.AI;
using AsistenteJuridico.Domain.Entities;
using Microsoft.Extensions.Options;

namespace AsistenteJuridico.Infrastructure.Services.AI;

/// <summary>
/// Fase 8.3 — Proveedor simulado de embeddings para desarrollo y pruebas (FASE_8_3_CONTRATO.md §16), sin red.
/// Algoritmo <c>mock-bow-sha256-v1</c>: bolsa de palabras con hashing SHA-256 sobre 1536 posiciones y normalización
/// L2. Determinista, independiente de la cultura y de la máquina, sin aleatoriedad y sin ganchos de fallo.
/// Su firma (mock:mock-bow-sha256-v1@1536) nunca coincide con la de un proveedor real. Prohibido en Production.
/// </summary>
public sealed class MockEmbeddingProvider : IEmbeddingProvider
{
    public const string Proveedor = "mock";
    public const string Modelo = "mock-bow-sha256-v1";

    public string ProviderId => Proveedor;
    public string ModelId => Modelo;
    public int Dimensiones => DocumentoIndice.DimensionesPerfilInicial;
    public int MaxTokensPorEntrada => EmbeddingOptions.MaxTokensPorEntrada;

    /// <summary>
    /// El mismo límite que el proveedor real: <c>AI:Embeddings:MaxEntradasPorLote</c> (por defecto y como máximo 64),
    /// aplicado con la misma validación común (<see cref="LoteEmbeddings.Validar"/>). Sin regla especial para el simulado.
    /// </summary>
    public int MaxEntradasPorLote { get; }

    public MockEmbeddingProvider(IOptions<EmbeddingOptions>? options = null)
    {
        MaxEntradasPorLote = LoteEmbeddings.LimiteEfectivo(options?.Value.MaxEntradasPorLote ?? LoteEmbeddings.MaxEntradasPorLoteContractual);
    }

    public Task<EmbeddingBatchResult> EmbedAsync(IReadOnlyList<string> entradas, EmbeddingPurpose proposito, CancellationToken ct)
    {
        LoteEmbeddings.Validar(entradas, MaxEntradasPorLote, MaxTokensPorEntrada);
        ct.ThrowIfCancellationRequested();

        var vectores = new List<float[]>(entradas.Count);
        var tokens = 0;
        foreach (var texto in entradas)
        {
            ct.ThrowIfCancellationRequested();
            vectores.Add(Vectorizar(texto));
            // El simulado es él mismo el proveedor: este es su consumo informado, no una estimación de otro proveedor.
            tokens += LoteEmbeddings.TokensEstimados(texto);
        }

        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new EmbeddingBatchResult(vectores, tokens, Modelo, Modelo, Proveedor, Dimensiones));
    }

    private float[] Vectorizar(string texto)
    {
        var acumulado = new double[Dimensiones];
        var primeraPosicion = -1;
        foreach (var token in Tokens(texto))
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
            var posicion = (int)(BinaryPrimitives.ReadUInt64LittleEndian(hash) % (ulong)Dimensiones);
            acumulado[posicion] += hash[8] % 2 == 0 ? 1 : -1;
            if (primeraPosicion < 0)
            {
                primeraPosicion = posicion;
            }
        }

        var norma = Norma(acumulado);
        if (norma == 0)
        {
            // Cancelación exacta de signos: nunca se devuelve un vector de norma 0.
            acumulado[primeraPosicion] = 1;
            norma = Norma(acumulado);
        }

        var vector = new float[Dimensiones];
        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] = (float)(acumulado[i] / norma);
        }

        return vector;
    }

    private static double Norma(double[] valores)
    {
        double suma = 0;
        foreach (var valor in valores)
        {
            suma += valor * valor;
        }

        return Math.Sqrt(suma);
    }

    /// <summary>
    /// Secuencias máximas de runes que son letra o dígito, en minúsculas invariantes. Si el texto no tiene ninguna
    /// (solo emojis o signos), cada rune que no sea espacio en blanco es un token.
    /// </summary>
    private static List<string> Tokens(string texto)
    {
        var tokens = new List<string>();
        var actual = new StringBuilder();
        foreach (var rune in texto.EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune))
            {
                actual.Append(Rune.ToLowerInvariant(rune).ToString());
            }
            else if (actual.Length > 0)
            {
                tokens.Add(actual.ToString());
                actual.Clear();
            }
        }

        if (actual.Length > 0)
        {
            tokens.Add(actual.ToString());
        }

        if (tokens.Count == 0)
        {
            foreach (var rune in texto.EnumerateRunes())
            {
                if (!Rune.IsWhiteSpace(rune))
                {
                    tokens.Add(rune.ToString());
                }
            }
        }

        return tokens;
    }
}
