#!/usr/bin/env python3
"""
Plot a cone-step reprojection margin-weight sweep produced by ConeStepMarginSweep.cs.

Usage:
    python plot_cone_step_margin_sweep.py cone_step_margin_sweep.csv

Produces, side by side:
  - a 1D heatmap strip (margin_weight on x), color = step count
  - a simple line plot: x = margin_weight, y = step_count
"""
import argparse
import sys

import numpy as np
import matplotlib.pyplot as plt


def load_sweep(path):
    margins, steps = [], []
    fixed_progress = None
    best_line = None

    with open(path, "r") as f:
        header = f.readline()  # skip CSV header
        for line in f:
            line = line.strip()
            if not line:
                continue
            if line.startswith("#"):
                best_line = line
                continue
            m, p, s = line.split(",")
            margins.append(float(m))
            steps.append(float(s))
            if fixed_progress is None:
                fixed_progress = float(p)

    if not margins:
        sys.exit(f"No data rows found in {path}")

    order = np.argsort(margins)
    margins = np.array(margins)[order]
    steps = np.array(steps)[order]

    return margins, steps, fixed_progress, best_line


def normalize(values, lo=None, hi=None):
    if lo is None:
        lo = values.min()
    if hi is None:
        hi = values.max()
    if hi - lo < 1e-12:
        return np.zeros_like(values)
    return np.clip((values - lo) / (hi - lo), 0, 1)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("csv_path", help="Path to the sweep CSV file")
    parser.add_argument("--raw", action="store_true",
        help="Color the heatmap by raw step count instead of normalized [0,1]")
    parser.add_argument("--percentile-clip", type=float, default=None, metavar="P",
        help="Clip heatmap color scale to [P, 100-P] percentiles to reduce outlier skew, e.g. --percentile-clip 2")
    args = parser.parse_args()

    margins, steps, fixed_progress, best_line = load_sweep(args.csv_path)

    if args.raw:
        display_values = steps
        c_label = "step count"
    elif args.percentile_clip is not None:
        lo = np.percentile(steps, args.percentile_clip)
        hi = np.percentile(steps, 100 - args.percentile_clip)
        display_values = normalize(steps, lo, hi)
        c_label = f"step count (clipped {args.percentile_clip:.0f}-{100 - args.percentile_clip:.0f}%ile)"
    else:
        display_values = normalize(steps)
        c_label = "step count (normalized 0-1)"

    if best_line:
        print(best_line.lstrip("# ").strip())
    else:
        min_idx = int(np.argmin(steps))
        print(f"best (computed): margin_weight={margins[min_idx]:.4f}, "
              f"progress_weight={fixed_progress:.4f}, step_count={steps[min_idx]:.0f}")

    min_idx = int(np.argmin(steps))

    fig, (ax0, ax1) = plt.subplots(2, 1, figsize=(9, 6), gridspec_kw={"height_ratios": [1, 4]})
    fig.suptitle(f"Cone-step margin-weight sweep (progress weight fixed at {fixed_progress:.3f})")

    # --- 1D heatmap strip ---
    extent = [margins.min(), margins.max(), 0, 1]
    im = ax0.imshow(display_values[np.newaxis, :], origin="lower", extent=extent,
                     aspect="auto", cmap="viridis")
    ax0.set_yticks([])
    ax0.set_xlabel("margin weight")
    ax0.axvline(margins[min_idx], color="red", linestyle="--", linewidth=1)
    fig.colorbar(im, ax=ax0, orientation="vertical", label=c_label, pad=0.02)

    # --- line plot ---
    ax1.plot(margins, steps, marker="o", markersize=3, linewidth=1.5)
    ax1.scatter([margins[min_idx]], [steps[min_idx]], color="red", zorder=5, label="min")
    ax1.set_xlabel("margin weight")
    ax1.set_ylabel("step count")
    ax1.set_title("Step count vs. margin weight")
    ax1.grid(True, alpha=0.3)
    ax1.legend()

    fig.tight_layout()
    plt.show()


if __name__ == "__main__":
    main()
