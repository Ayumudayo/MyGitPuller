# MyGitPuller

A Python script to efficiently update multiple Git repositories in parallel. It recursively scans for all repositories within its parent directory, pulls the latest changes, and updates any associated submodules.

...is just a long-winded way of saying...
I wanted to keep the repositories cloned locally up to date, and since updating them one by one was cumbersome, I created this.

## Features

- **Parallel Updates:** Uses multithreading to update many repositories at once, significantly speeding up the process.
- **Submodule Support:** Automatically initializes and updates Git submodules (`git submodule update --init --recursive`).
- **Smart Caching:** Caches the list of repository paths in a `.git_repo_cache.json` file for near-instantaneous startup on subsequent runs.
- **Automatic Cache Cleaning:** Automatically detects and removes invalid or deleted repository paths from the cache.
- **Configurable Parallelism:** Allows you to customize the number of parallel workers.
- **Low System Priority:** Runs Git commands with a lower process priority to minimize impact on system performance.
- **Clear Status Reports:** Provides detailed, color-coded feedback on the status of each repository (Success, Up-to-date, Failed, or Timeout).

## Usage

1.  Place the `pull.py` script in a parent directory that contains all the Git project folders you want to manage.
    
    ```
    /MyProjects/
    ├── /ProjectA/ (.git)
    ├── /ProjectB/ (.git)
    ├── /AnotherRepo/ (.git)
    └── pull.py
    ```
    
2.  Run the script from your terminal in that directory.

    ```bash
    python pull.py [OPTIONS]
    ```

## Options

-   `-w, --max-workers <NUMBER>`
    
    Sets the maximum number of parallel processes to run. The default value is half the number of available CPU cores.
    
    *Example:*
    
    ```bash
    # Run with up to 8 parallel workers
    python pull.py --max-workers 8
    ```
    
-   `-r, --refresh`
    
    Forces a full, recursive rescan of all directories to find repositories, ignoring the existing cache. This is useful if you have added or moved repositories.
    
    *Example:*
    
    ```bash
    # Re-scan all folders and update the cache
    python pull.py --refresh
    ```

## How It Works

1.  **First Run:** On the first execution (or when using `--refresh`), the script scans all subdirectories to locate every Git repository and saves this list to a `.git_repo_cache.json` file.
2.  **Subsequent Runs:** On future runs, it loads the repository list directly from the cache file for a much faster start. It also validates each path and automatically cleans the cache if any repositories have been moved or deleted.
3.  **Execution:** The script uses a thread pool to execute `git pull` and `git submodule update` commands on all found repositories in parallel, printing the status and output for each one.
