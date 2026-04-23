using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Btl.Whiteboard.Contracts;

internal sealed partial class WhiteboardServer
{
	private long NextSequence()
	{
		return Interlocked.Increment(ref sequence);
	}

	private static StrokeCommandPayload Convert(StrokeStartPayload payload, string eventType)
	{
		return new StrokeCommandPayload(
			eventType,
			payload.StrokeId,
			payload.UserId,
			payload.UserName,
			payload.ColorHex,
			payload.Thickness,
			payload.LayerId,
			payload.X,
			payload.Y);
	}

	private static StrokeCommandPayload Convert(StrokePointPayload payload, string eventType)
	{
		return new StrokeCommandPayload(
			eventType,
			payload.StrokeId,
			payload.UserId,
			payload.UserName,
			payload.ColorHex,
			payload.Thickness,
			payload.LayerId,
			payload.X,
			payload.Y);
	}

	private static StrokeCommandPayload Convert(StrokeEndPayload payload, string eventType)
	{
		return new StrokeCommandPayload(
			eventType,
			payload.StrokeId,
			payload.UserId,
			payload.UserName,
			payload.ColorHex,
			payload.Thickness,
			payload.LayerId,
			payload.X,
			payload.Y);
	}

	private bool TryApplyStrokeCommand(
		ConnectedClient sender,
		WhiteboardMessageType messageType,
		StrokeCommandPayload command,
		out StrokeCommandPayload normalizedCommand)
	{
		normalizedCommand = command with
		{
			UserName = sender.UserName,
			ColorHex = NormalizeColorHex(command.ColorHex),
			Thickness = NormalizeStrokeThickness(command.Thickness),
			LayerId = NormalizeLayerId(command.LayerId)
		};

		lock (historyLock)
		{
			switch (messageType)
			{
				case WhiteboardMessageType.StrokeStart:
					if (activeStrokeOwners.ContainsKey(normalizedCommand.StrokeId))
					{
						return false;
					}

					activeStrokeOwners[normalizedCommand.StrokeId] = sender.UserId;
					strokeHistory.Add(normalizedCommand);
					TrimHistoryIfNeeded();
					return true;

				case WhiteboardMessageType.StrokePoint:
					if (!activeStrokeOwners.TryGetValue(normalizedCommand.StrokeId, out string? pointOwnerId) ||
						!string.Equals(pointOwnerId, sender.UserId, StringComparison.Ordinal))
					{
						return false;
					}

					strokeHistory.Add(normalizedCommand);
					TrimHistoryIfNeeded();
					return true;

				case WhiteboardMessageType.StrokeEnd:
					if (!activeStrokeOwners.TryGetValue(normalizedCommand.StrokeId, out string? endOwnerId) ||
						!string.Equals(endOwnerId, sender.UserId, StringComparison.Ordinal))
					{
						return false;
					}

					activeStrokeOwners.Remove(normalizedCommand.StrokeId);
					strokeHistory.Add(normalizedCommand);
					TrimHistoryIfNeeded();
					redoHistory.Clear();
					return true;

				default:
					return false;
			}
		}
	}

	private bool HasActiveStrokeInProgress()
	{
		lock (historyLock)
		{
			return activeStrokeOwners.Count > 0;
		}
	}

	private bool RemoveActiveStrokesForUser(string userId)
	{
		lock (historyLock)
		{
			string[] danglingStrokeIds = activeStrokeOwners
				.Where(pair => string.Equals(pair.Value, userId, StringComparison.Ordinal))
				.Select(pair => pair.Key)
				.ToArray();

			if (danglingStrokeIds.Length == 0)
			{
				return false;
			}

			HashSet<string> danglingSet = new(danglingStrokeIds, StringComparer.Ordinal);
			foreach (string strokeId in danglingStrokeIds)
			{
				activeStrokeOwners.Remove(strokeId);
			}

			int removedCommands = strokeHistory.RemoveAll(command => danglingSet.Contains(command.StrokeId));
			if (removedCommands > 0)
			{
				redoHistory.Clear();
			}

			return removedCommands > 0;
		}
	}

	private bool TryUndoLastStrokeForUser(string userId)
	{
		lock (historyLock)
		{
			string? strokeId = null;
			for (int index = strokeHistory.Count - 1; index >= 0; index--)
			{
				StrokeCommandPayload command = strokeHistory[index];
				if (string.Equals(command.EventType, "end", StringComparison.Ordinal) &&
					string.Equals(command.UserId, userId, StringComparison.Ordinal))
				{
					strokeId = command.StrokeId;
					break;
				}
			}

			if (string.IsNullOrWhiteSpace(strokeId))
			{
				return false;
			}

			List<StrokeCommandPayload> removedCommands = strokeHistory
				.Where(command => string.Equals(command.StrokeId, strokeId, StringComparison.Ordinal))
				.ToList();

			if (removedCommands.Count == 0)
			{
				return false;
			}

			strokeHistory.RemoveAll(command => string.Equals(command.StrokeId, strokeId, StringComparison.Ordinal));
			redoHistory.Push(removedCommands);
			return true;
		}
	}

	private bool TryRedoLastStrokeForUser(string userId)
	{
		lock (historyLock)
		{
			if (redoHistory.Count == 0)
			{
				return false;
			}

			Stack<List<StrokeCommandPayload>> tempStack = new();
			List<StrokeCommandPayload>? foundCommands = null;

			while (redoHistory.Count > 0)
			{
				List<StrokeCommandPayload> commands = redoHistory.Pop();
				string? ownerId = commands.FirstOrDefault()?.UserId;

				if (foundCommands is null && string.Equals(ownerId, userId, StringComparison.Ordinal))
				{
					foundCommands = commands;
				}
				else
				{
					tempStack.Push(commands);
				}
			}

			while (tempStack.Count > 0)
			{
				redoHistory.Push(tempStack.Pop());
			}

			if (foundCommands is null)
			{
				return false;
			}

			strokeHistory.AddRange(foundCommands);
			TrimHistoryIfNeeded();
			return true;
		}
	}

	private void ClearRedoHistory()
	{
		lock (historyLock)
		{
			redoHistory.Clear();
		}
	}

	private void AddStrokeCommand(StrokeCommandPayload command)
	{
		lock (historyLock)
		{
			strokeHistory.Add(command);
			TrimHistoryIfNeeded();
		}
	}

	private void TrimHistoryIfNeeded()
	{
		if (strokeHistory.Count <= MaxHistoryCommands)
		{
			return;
		}

		int removeCount = strokeHistory.Count - TrimHistoryToCommands;
		if (removeCount > 0)
		{
			strokeHistory.RemoveRange(0, removeCount);
		}
	}

	private static string NormalizeUserName(string userName, string fallback)
	{
		string safe = (userName ?? string.Empty).Trim();
		if (safe.Length == 0)
		{
			safe = fallback;
		}

		if (safe.Length > MaxUserNameLength)
		{
			safe = safe[..MaxUserNameLength];
		}

		return safe;
	}

	private static void ValidateStrokeMessage(string strokeId, double x, double y)
	{
		if (string.IsNullOrWhiteSpace(strokeId) || strokeId.Length > MaxStrokeIdLength)
		{
			throw new InvalidDataException("Stroke identifier is invalid.");
		}

		if (!IsFinite(x) || !IsFinite(y))
		{
			throw new InvalidDataException("Stroke coordinates must be finite values.");
		}
	}

	private static bool IsFinite(double value)
	{
		return !(double.IsNaN(value) || double.IsInfinity(value));
	}

	private static string NormalizeColorHex(string colorHex)
	{
		string safe = (colorHex ?? string.Empty).Trim();
		if (safe.Length == 0)
		{
			return "#1F6FEB";
		}

		if (safe[0] != '#')
		{
			safe = $"#{safe}";
		}

		if (safe.Length > MaxColorHexLength || !IsHexColor(safe))
		{
			return "#1F6FEB";
		}

		return safe.ToUpperInvariant();
	}

	private static double NormalizeStrokeThickness(double thickness)
	{
		if (!IsFinite(thickness))
		{
			return 2.4;
		}

		if (thickness < MinStrokeThickness)
		{
			return MinStrokeThickness;
		}

		if (thickness > MaxStrokeThickness)
		{
			return MaxStrokeThickness;
		}

		return thickness;
	}

	private static string NormalizeLayerId(string layerId)
	{
		string safe = (layerId ?? string.Empty).Trim();
		if (safe.Length == 0)
		{
			return "layer-1";
		}

		if (safe.Length > MaxLayerIdLength)
		{
			safe = safe[..MaxLayerIdLength];
		}

		return safe;
	}

	private static bool IsHexColor(string colorHex)
	{
		if (string.IsNullOrWhiteSpace(colorHex) || colorHex[0] != '#')
		{
			return false;
		}

		ReadOnlySpan<char> digits = colorHex.AsSpan(1);
		if (digits.Length is not (6 or 8))
		{
			return false;
		}

		foreach (char digit in digits)
		{
			if (!Uri.IsHexDigit(digit))
			{
				return false;
			}
		}

		return true;
	}
}
