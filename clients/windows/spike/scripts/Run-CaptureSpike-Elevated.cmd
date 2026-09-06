@echo off
rem ---------------------------------------------------------------------------------------
rem  Double-click this file to run the M1 capture spike.
rem
rem  It exists because the spike needs Add-Printer, Add-Printer needs elevation, and a
rem  self-elevating one-liner pasted into a terminal is easy to break on a line wrap - which
rem  is exactly how the first attempt was lost. A file you double-click cannot be mis-pasted.
rem
rem  Approve the UAC prompt. A window opens, builds the listener, creates the
rem  Printo-Spike-IPP queue, and waits for you to print one page to it from Chrome. It
rem  removes the queue again on its way out, whatever happens.
rem ---------------------------------------------------------------------------------------
powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process powershell -Verb RunAs -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-NoExit','-File','%~dp0Run-CaptureSpike.ps1'"
