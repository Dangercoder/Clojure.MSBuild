namespace CSharpInterop;

public class MathService
{
    public static double CircleArea(double radius) => System.Math.PI * radius * radius;

    public static string Greet(string name) => $"Hello from C#, {name}!";

    public static int[] Fibonacci(int n)
    {
        if (n <= 0) return [];
        if (n == 1) return [0];
        var result = new int[n];
        result[0] = 0;
        result[1] = 1;
        for (int i = 2; i < n; i++)
            result[i] = result[i - 1] + result[i - 2];
        return result;
    }
}

public class ClojureBridge
{
    public static object CallClojure(string ns, string fn, object arg)
    {
        var cljFn = clojure.lang.RT.var(ns, fn);
        return cljFn.invoke(arg);
    }

    public static string AnalyzeFromCSharp(int[] data)
    {
        var result = CallClojure("app", "analyze", clojure.lang.RT.seq(data));
        return result?.ToString() ?? "nil";
    }
}
