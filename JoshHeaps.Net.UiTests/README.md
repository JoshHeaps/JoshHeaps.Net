# JoshHeaps.Net UI Tests

This project contains end-to-end tests for the JoshHeaps.Net website using Playwright.

## Setup

1. Install Playwright browsers:
   ```bash
   # From the test project directory
   dotnet build
   # Then install browsers (requires PowerShell or appropriate command for your OS)
   bin/Debug/net8.0/playwright.cmd install  # Windows
   ```

2. Make sure the main application is running on `https://localhost:7065`

## Running Tests

```bash
# Run all tests
dotnet test

# Run specific test file
dotnet test --filter "FullyQualifiedName~ChessGameTests"

# Run with verbose output
dotnet test --logger "console;verbosity=detailed"
```

## Test Categories

### ChessGameTests
- Basic chess page functionality
- Game start/stop operations
- Piece interaction and move validation
- UI element verification

### MultiplayerTests
- Two-player game scenarios
- SignalR connection testing
- Real-time move synchronization

### ApiTests
- Chess API endpoint testing
- Game creation and state management
- Move validation at API level
- Error handling

## Configuration

The tests are configured to:
- Use headless browsers by default
- Take screenshots on failure
- Record videos on failure
- Automatically start the web server if needed (port 7065)

## Notes

- Tests expect the main application to be available at `https://localhost:7065`
- Some tests require browsers to be installed via Playwright CLI
- Browser installations may require PowerShell on Windows systems