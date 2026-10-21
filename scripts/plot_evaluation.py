"""Plot comparison charts for PlanEvaluation output.

Reads the results.csv + summary.csv that PlanEvaluation writes and emits
four PNGs into the same directory:

    error_count_box.png     per-mode distribution of validator errors
    mav_deviation_box.png   per-mode distance from MAV midpoint
    latency_bar.png         mean latency + 1 sd error bars
    cost_bar.png            mean estimated $ per generated plan

Usage:
    python scripts/plot_evaluation.py [--input DIR]

Dependencies: pandas, matplotlib. Install once with:
    pip install pandas matplotlib
"""
from __future__ import annotations

import argparse
import os
import sys

import matplotlib.pyplot as plt
import pandas as pd

MODES = ["Constraint", "LlmOnly", "Hybrid"]
MODE_COLORS = {"Constraint": "#4C72B0", "LlmOnly": "#DD8452", "Hybrid": "#55A868"}


def load(input_dir: str) -> tuple[pd.DataFrame, pd.DataFrame]:
    results_path = os.path.join(input_dir, "results.csv")
    summary_path = os.path.join(input_dir, "summary.csv")
    if not os.path.exists(results_path):
        sys.exit(f"not found: {results_path}")
    if not os.path.exists(summary_path):
        sys.exit(f"not found: {summary_path}")
    return pd.read_csv(results_path), pd.read_csv(summary_path)


def boxplot(results: pd.DataFrame, column: str, title: str, ylabel: str, out: str) -> None:
    data = [results.loc[results["mode"] == m, column].values for m in MODES]
    fig, ax = plt.subplots(figsize=(6, 4))
    bp = ax.boxplot(data, patch_artist=True, labels=MODES)
    for patch, mode in zip(bp["boxes"], MODES):
        patch.set_facecolor(MODE_COLORS[mode])
        patch.set_alpha(0.75)
    ax.set_title(title)
    ax.set_ylabel(ylabel)
    ax.grid(True, axis="y", alpha=0.3)
    fig.tight_layout()
    fig.savefig(out, dpi=150)
    plt.close(fig)
    print(f"  wrote {out}")


def bar_with_sd(summary: pd.DataFrame, mean_col: str, sd_col: str | None,
                title: str, ylabel: str, out: str) -> None:
    rows = summary.set_index("mode").reindex(MODES)
    means = rows[mean_col].values
    sds = rows[sd_col].values if sd_col and sd_col in rows else None

    fig, ax = plt.subplots(figsize=(6, 4))
    colors = [MODE_COLORS[m] for m in MODES]
    ax.bar(MODES, means, yerr=sds, capsize=6, color=colors, alpha=0.85, edgecolor="black")
    ax.set_title(title)
    ax.set_ylabel(ylabel)
    ax.grid(True, axis="y", alpha=0.3)
    fig.tight_layout()
    fig.savefig(out, dpi=150)
    plt.close(fig)
    print(f"  wrote {out}")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--input", default="eval-output",
                        help="directory holding results.csv + summary.csv")
    args = parser.parse_args()

    results, summary = load(args.input)
    out = args.input

    boxplot(results, "error_count",   "Validator errors per plan",
            "errors", os.path.join(out, "error_count_box.png"))
    boxplot(results, "mav_deviation", "Weekly-sets deviation from MAV midpoint",
            "sum |V - MAV|", os.path.join(out, "mav_deviation_box.png"))

    bar_with_sd(summary, "latency_ms_mean", "latency_ms_sd",
                "Mean latency (ms) per generated plan",
                "ms", os.path.join(out, "latency_bar.png"))
    bar_with_sd(summary, "cost_usd_mean", None,
                "Mean estimated cost (USD) per generated plan",
                "USD", os.path.join(out, "cost_bar.png"))

    print("\nSummary (mean values):")
    cols = ["mode", "n", "error_count_mean", "warning_count_mean",
            "mav_deviation_mean", "latency_ms_mean", "cost_usd_mean"]
    print(summary[cols].to_string(index=False))


if __name__ == "__main__":
    main()
