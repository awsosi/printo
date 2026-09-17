using System.Runtime.ExceptionServices;

namespace Printo.Agent.Tests;

/// <summary>Runs WinForms code on a thread that can host it.</summary>
/// <remarks>
/// Test threads are MTA, and a form created there fails in ways that look like product defects
/// (clipboard, drag and drop, some common controls). An exception thrown on the UI thread is
/// rethrown here with its original stack, so a failing assertion inside still reads correctly.
/// </remarks>
internal static class UiThread
{
    public static void Run(Action action)
    {
        ExceptionDispatchInfo? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception error)
            {
                failure = ExceptionDispatchInfo.Capture(error);
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        failure?.Throw();
    }
}
