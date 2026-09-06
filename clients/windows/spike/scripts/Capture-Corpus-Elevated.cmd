@echo off
rem ---------------------------------------------------------------------------------------
rem  Double-click this file to capture the multi-document spread.
rem
rem  A fresh elevated window opens for it, so there is no chance of the command landing in a
rem  terminal that is still running something else - which is how the last attempt was lost.
rem
rem  Approve the UAC prompt. Chrome then opens seven documents one at a time; press Ctrl+P
rem  then Enter for each. The spike queue is the default printer for the duration, so Enter
rem  is enough - you do not need to choose a destination.
rem ---------------------------------------------------------------------------------------
powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process powershell -Verb RunAs -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-NoExit','-File','%~dp0Capture-Corpus.ps1'"
