import subprocess
import concurrent.futures
from pathlib import Path
import argparse
import os
import sys
import json

def run_git_commands(repo_path: Path, timeout: int = 60):
    """
    Runs git pull and submodule update, and returns the status and output.
    """
    if not (repo_path / ".git").is_dir():
        return repo_path, "Not a Git repository", ""

    # Lower the priority of child processes on Windows/Linux/macOS to reduce system load
    kwargs = {}
    if sys.platform == "win32":
        # BELOW_NORMAL_PRIORITY_CLASS: Lower than normal priority
        kwargs['creationflags'] = subprocess.BELOW_NORMAL_PRIORITY_CLASS
    elif sys.platform in ["linux", "darwin"]:
        # For os.nice(), a higher value means a lower priority
        kwargs['preexec_fn'] = lambda: os.nice(10)

    try:
        # Run git pull (check=False does not raise an exception if the returncode is non-zero)
        pull_proc = subprocess.run(
            ["git", "pull"], cwd=repo_path, capture_output=True, text=True,
            timeout=timeout, check=False, **kwargs
        )
        pull_output = pull_proc.stdout + pull_proc.stderr

        # Run git submodule update
        submodule_proc = subprocess.run(
            ["git", "submodule", "update", "--init", "--recursive"],
            cwd=repo_path, capture_output=True, text=True,
            timeout=timeout, check=False, **kwargs
        )
        submodule_output = submodule_proc.stdout + submodule_proc.stderr

        is_pull_ok = pull_proc.returncode == 0
        is_submodule_ok = submodule_proc.returncode == 0

        combined_output = (
            f"--- git pull ---\n{pull_output.strip()}"
            + (f"\n--- submodules ---\n{submodule_output.strip()}" if submodule_output.strip() else "")
        )

        if not is_pull_ok or not is_submodule_ok:
            return repo_path, "Failed", combined_output

        is_pull_uptodate = "Already up to date." in pull_output
        is_submodule_unchanged = not submodule_output.strip()

        if is_pull_uptodate and is_submodule_unchanged:
            return repo_path, "Up-to-date", ""
        
        return repo_path, "Success", combined_output

    except subprocess.TimeoutExpired as e:
        return repo_path, "Timeout", f"Command timed out: {e}"
    except Exception as e:
        return repo_path, "Error", f"An unexpected error occurred: {e}"

def find_and_cache_repositories(script_dir: Path, cache_file: Path):
    """
    Scans the entire directory to find Git repositories and saves the results to a cache file.
    """
    print("Scanning all directories to find Git repositories...")
    directories = [p for p in script_dir.iterdir() if p.is_dir()]
    
    repos = []
    for directory in directories:
        # Use rglob to efficiently search for .git folders in all subdirectories
        for git_dir in directory.rglob(".git"):
            if git_dir.is_dir():
                repos.append(git_dir.parent)
    
    # Remove duplicates with set and sort for consistency
    sorted_repos = sorted(list(set(repos)))
    
    try:
        with cache_file.open("w") as f:
            json.dump([str(p) for p in sorted_repos], f, indent=2)
        print(f"Found {len(sorted_repos)} repositories. Cache file created/updated at: {cache_file.name}")
    except IOError as e:
        print(f"Error: Could not write to cache file: {e}")
        
    return sorted_repos

def load_repositories_from_cache(cache_file: Path):
    """
    Reads the list of repositories from the cache file and automatically removes invalid paths.
    """
    print(f"Loading repository list from cache: {cache_file.name}")
    try:
        with cache_file.open("r") as f:
            repo_paths_str = json.load(f)
    except (json.JSONDecodeError, IOError) as e:
        print(f"Error: Could not read cache file ({e}). Run with --refresh to rebuild it.")
        return []

    valid_repos = []
    original_count = len(repo_paths_str)

    # Check if each cached path is still a valid Git repository
    for p_str in repo_paths_str:
        repo_path = Path(p_str)
        if repo_path.is_dir() and (repo_path / ".git").is_dir():
            valid_repos.append(repo_path)
        else:
            print(f"Warning: Cached path is no longer a valid git repo, removing: {p_str}")
    
    # If there were invalid paths, clean up and overwrite the cache file (auto-cleanup feature)
    if len(valid_repos) != original_count:
        print("Cleaning invalid paths from cache file...")
        try:
            with cache_file.open("w") as f:
                json.dump([str(p) for p in valid_repos], f, indent=2)
            print("Cache file has been cleaned.")
        except IOError as e:
            print(f"Error: Could not update cache file: {e}")

    print(f"Loaded {len(valid_repos)} valid repositories from cache.")
    return valid_repos

def print_separator():
    print("-" * 60)

def positive_int_range(min_val, max_val):
    """
    Custom type function for argparse.
    Checks if the argument value is an integer within the specified range [min_val, max_val].
    """
    # Returns a checker function that remembers min_val and max_val using a closure
    def checker(value_str):
        try:
            value = int(value_str)
            if not (min_val <= value <= max_val):
                raise ValueError(f"Value must be between {min_val} and {max_val}.")
        except ValueError as e:
            # Convert the exception to ArgumentTypeError so argparse can handle it
            raise argparse.ArgumentTypeError(str(e))
        return value
    return checker

def main():
    """
    Main execution function
    """
    # --- 1. Set up argument parser ---
    parser = argparse.ArgumentParser(description="Update Git repositories using a cache for speed.")
    
    # --max-workers: Number of parallel jobs. Default is half the system's CPU cores.
    # (os.cpu_count() or 1) is an exception handler in case cpu_count() returns None
    default_workers = max(1, (os.cpu_count() or 1) // 2)
    parser.add_argument(
        "-w", "--max-workers", 
        type=positive_int_range(1, 64), # Only allow values between 1 and 64
        default=default_workers,
        help=f"Maximum number of parallel processes (1-64). (Default: {default_workers})"
    )
    # --refresh: Ignore the cache and force a directory rescan
    parser.add_argument(
        "-r", "--refresh", action="store_true",
        help="Force a full rescan of directories to refresh the repository cache."
    )
    args = parser.parse_args()

    if args.max_workers < 1:
        print("Error: --max-workers must be at least 1.")
        sys.exit(1)

    if args.max_workers > os.cpu_count():
        print(f"Warning: --max-workers is set to {args.max_workers}, which exceeds the number of available CPU cores ({os.cpu_count()}). This may lead to suboptimal performance.")
        print("Consider reducing the number of workers for better performance.")
        sys.exit(1)

    script_dir = Path(__file__).resolve().parent
    cache_file = script_dir / ".git_repo_cache.json"
    
    # --- 2. Prepare repository list (cache first) ---
    repos = []
    # If --refresh option is used or cache file doesn't exist (first run), perform a full scan
    if args.refresh or not cache_file.is_file():
        if not cache_file.is_file():
            print("Cache file not found. Performing initial scan...")
        else:
            print("Forcing cache refresh...")
        repos = find_and_cache_repositories(script_dir, cache_file)
    else:
        # If cache file exists, load the list from cache (fastest path)
        repos = load_repositories_from_cache(cache_file)

    total = len(repos)
    if total == 0:
        print("No repositories to update.")
        return
        
    print(f"\nStarting updates for {total} repositories with up to {args.max_workers} parallel workers...")
    print_separator()

    # --- 3. Run Git commands in parallel ---
    completed = 0
    # Use ThreadPoolExecutor to run tasks in parallel with the specified number of max_workers
    with concurrent.futures.ThreadPoolExecutor(max_workers=args.max_workers) as executor:
        future_to_repo = {executor.submit(run_git_commands, repo): repo for repo in repos}
        
        for future in concurrent.futures.as_completed(future_to_repo):
            repo, status, output = future.result()
            completed += 1
            
            # Skip output for up-to-date repositories to keep it clean
            if status == "Up-to-date":
                continue

            progress = f"[{completed}/{total}]"
            icon = "✅" if status == "Success" else "⏰" if status == "Timeout" else "❌"
            
            try:
                # Print relative path from the script location for readability
                relative_path = repo.relative_to(script_dir)
            except ValueError:
                relative_path = repo # Use absolute path if relative path cannot be created (e.g., different drive)

            print(f"{icon} {progress} Repository: {relative_path}")
            print(f"Status: {status}")
            if output:
                print("Details:")
                print(output)
            print_separator()

    print("All tasks completed.")


if __name__ == "__main__":
    main()
