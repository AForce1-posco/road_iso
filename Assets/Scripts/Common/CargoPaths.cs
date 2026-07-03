using System.IO;
using UnityEngine;

/// <summary>
/// 프로젝트 데이터 경로 중앙 정의. (에디터 전용 리서치 도구라 Application.dataPath 기준)
///  - Cases      : 정식 배치 데이터 (동적 씬 기본 루프 대상)
///  - TestCases  : 정적 씬에서 직접 담아 저장한 테스트용 (test_ 접두어)
///  - Results    : 동적 주행 결과 CSV (요약 results.csv + 통합 시계열)
///  - CatalogCsv : 화물 스펙 표 (하드코딩 대신 이 CSV에서 로드)
/// </summary>
public static class CargoPaths
{
    public static string DataDir => Path.Combine(Application.dataPath, "Data");
    public static string CasesDir => Path.Combine(DataDir, "Cases");
    public static string TestCasesDir => Path.Combine(DataDir, "TestCases");
    public static string ResultsDir => Path.Combine(DataDir, "Results");
    public static string CatalogCsv => Path.Combine(DataDir, "cargo_catalog.csv");

    public static void EnsureAll()
    {
        Directory.CreateDirectory(CasesDir);
        Directory.CreateDirectory(TestCasesDir);
        Directory.CreateDirectory(ResultsDir);
    }
}
