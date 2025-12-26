# SBridge (Paper Artifact)

[![DOI](https://zenodo.org/badge/DOI/10.5281/17101651.svg)](https://doi.org/10.5281/zenodo.17101651)

## Setup

> **Note:** Building the image may take a long time and the resulting Docker image size is approximately **67GB** (mainly due to the large RQ4 dataset).
> Please ensure you have sufficient disk space before starting the build.

It is recommended to use Docker.

1. Clone this repository and download the dataset (save it in the same directory & do not extract the .tar file)
   Dataset Download : https://drive.google.com/file/d/1ZuHISRLGi2k6htwEF5tbC0hu6kP3XUK5/view (Including preprocessed files)
    ```bash
     git clone https://github.com/heedongy/SBridge_Artifact
     ```


2. Build the image by running either:
   - `./build.sh`
   - or the Docker command:
     ```bash
     docker build -t sbridge_artifact:latest .
     ```
   *(Note: Building the image may take a significant amount of time.)*

3. To run the container, execute either:
   - `./run_image.sh`
   - or the Docker command:
     ```bash
     docker run -it --rm --name sbridge_container sbridge_artifact:latest /bin/bash
     ```
### **Sample Function Matching and Vulnerability Detection**:
 3. To run sample function matching:
    - Sample: coreutils, CAT
        ```
        /home/user/sample_match.sh
        ```

 4. To run sample vulnerability detection
   - Sample: CVE-2022-40303:
        ```
        /home/user/sample_vul_detect.sh
        ```
## Usage (Simple Version)

### **Executable Location:**
  SBridge is compiled with .NET 9.0. The executable file is located at:
    `/home/user/SBridge/bin/Release/net9.0/sbridge`  (in docker image)

### **Coreutils/Inetutils Execution (RQ1)**:
 - To run the coreutils or inetutils mentioned in the paper (RQ1), use the following script.

- For example, to run the `head` binary of coretuils:
    ```
    /home/user/coreutils_match.sh [binary]
    ```
    ex : `/home/user/coreutils_match.sh head `

- For example, to run the `dnsdomainname` binary of inetuils:
    ```
    /home/user/coreutils_match.sh [binary]
    ```
    ex : `/home/user/inetutils_match.sh dnsdomainname `




# SBridge info


 ```tree
project_folder
│
├── input (folder)
│   ├── vulcode (folder)        # source files of the project before the patch (only vulnerable code)
│   ├── patchcode (folder)      # source files of the project after the patch (only function matchin)
│   └── matchcode (folder)     # Source code files used for function matching c
│
├── target (folder)
│   └── binaryfiles   # Unknown binaries (vulnerable, patched, stripped binaries, etc.)
│
└── target.info       # Text file listing vulnerable function names (one name per line)

* Note : If preprocessed files (`*.i`) are available, include them in the input directory for analysis.
 ```


## The usage of SBridge

- The usage of the SBridge tool is as follows:

1. SBridge Build
   ```bash
   dotnet build -c Release
    ```

2. Joren Paser Server Start (port:8081)
    ```bash
    /home/user/SBridge/start_joernserver.sh
    ```


3. Source Code Parsing and Analysis
    ```bash
    /home/user/SBridge/bin/Release/net9.0/sbridge -s <project_path> (--clean) # clean is optional, default is false
    ```
    ex) `/home/user/SBridge/bin/Release/net9.0/sbridge -s dataset/sample/cat --clean`

4. Binary Code Parsing and Analysis (May take a long time)

   - The parsing step using the **Joern parser** can take a significant amount of time.
   - Note that SBridge's own preprocessing does **not** take long.

    ```bash
    /home/user/SBridge/bin/Release/net9.0/sbridge -b <project_path> (--ida) (--clean) # IDA Pro is optional, default is Ghidra
    ```
     ex) `/home/user/SBridge/bin/Release/net9.0/sbridge -b dataset/sample/cat --clean`

5.  SBridge Function Matching (Source to Binary)
    ```bash
    /home/user/SBridge/bin/Release/net9.0/sbridge -m <project_path> (--ida)  # IDA Pro is optional, default is Ghidra
    ```
     ex) `/home/user/SBridge/bin/Release/net9.0/sbridge -m dataset/sample/cat`

6. Vulnerability Detection
    ```bash
    /home/user/SBridge/bin/Release/net9.0/sbridge -d <project_path> (--ida) # IDA Pro is optional, default is Ghidra
    ```
     ex) `/home/user/SBridge/bin/Release/net9.0/sbridge -d dataset/sample/CVE-2018-14470`
