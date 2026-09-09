#!/usr/bin/env python3
"""
Plot a cone-step reprojection weight sweep produced by ConeStepWeightSweep.cs.

Usage:
    python plot_cone_step_sweep.py cone_step_sweep.csv

Produces, side by side:
  - a 2D heatmap (margin_weight x progress_weight), color = normalized step count
  - a 3D surface plot with the same normalized step count on the Z axis

Both use the same normalization (min/max of the observed data -> [0, 1]) so they're
directly comparable.
"""
import argparse
import sys

import numpy as np
import matplotlib.pyplot as plt
from mpl_toolkits.mplot3d import Axes3D  # noqa: F401 (needed for 3d projection)


def load_sweep(path):
    margins, progresses, steps = [], [], []
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
            progresses.append(float(p))
            steps.append(float(s))

    if not margins:
        sys.exit(f"No data rows found in {path}")

    return np.array(margins), np.array(progresses), np.array(steps), best_line


def to_grid(margins, progresses, steps):
    margin_axis = np.unique(margins)
    progress_axis = np.unique(progresses)

    if len(margin_axis) * len(progress_axis) != len(steps):
        sys.exit(
            "Data does not form a complete rectangular grid "
            f"({len(margin_axis)} x {len(progress_axis)} != {len(steps)} rows). "
            "Did the sweep finish, or was gridResolution changed mid-run?"
        )

    grid = np.zeros((len(progress_axis), len(margin_axis)))  # rows=progress, cols=margin
    margin_index = {v: i for i, v in enumerate(margin_axis)}
    progress_index = {v: i for i, v in enumerate(progress_axis)}

    for m, p, s in zip(margins, progresses, steps):
        grid[progress_index[p], margin_index[m]] = s

    return margin_axis, progress_axis, grid


def normalize(grid, lo=None, hi=None):
    if lo is None:
        lo = grid.min()
    if hi is None:
        hi = grid.max()
    if hi - lo < 1e-12:
        return np.zeros_like(grid)
    return np.clip((grid - lo) / (hi - lo), 0, 1)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("csv_path", help="Path to the sweep CSV file")
    parser.add_argument("--raw", action="store_true",
        help="Color/height by raw step count instead of normalized [0,1]")
    parser.add_argument("--percentile-clip", type=float, default=None, metavar="P",
        help="Clip color scale to [P, 100-P] percentiles to reduce outlier skew, e.g. --percentile-clip 2")
    args = parser.parse_args()

    margins, progresses, steps, best_line = load_sweep(args.csv_path)
    margin_axis, progress_axis, grid = to_grid(margins, progresses, steps)

    if args.raw:
        display_grid = grid
        z_label = "step count"
    elif args.percentile_clip is not None:
        lo = np.percentile(grid, args.percentile_clip)
        hi = np.percentile(grid, 100 - args.percentile_clip)
        display_grid = normalize(grid, lo, hi)
        z_label = f"step count (clipped {args.percentile_clip:.0f}-{100-args.percentile_clip:.0f}%ile)"
    else:
        display_grid = normalize(grid)
        z_label = "step count (normalized 0-1)"

    if best_line:
        print(best_line.lstrip("# ").strip())
    else:
        flat_min_idx = np.unravel_index(np.argmin(grid), grid.shape)
        print(f"best (computed): margin_weight={margin_axis[flat_min_idx[1]]:.4f}, "
            f"progress_weight={progress_axis[flat_min_idx[0]]:.4f}, "
            f"step_count={grid[flat_min_idx]:.0f}")

    fig = plt.figure(figsize=(13, 5.5))

    # --- 2D heatmap ---
    ax0 = fig.add_subplot(1, 2, 1)
    extent = [margin_axis.min(), margin_axis.max(), progress_axis.min(), progress_axis.max()]
    im = ax0.imshow(display_grid, origin="lower", extent=extent, aspect="auto", cmap="viridis")
    ax0.set_xlabel("margin weight")
    ax0.set_ylabel("progress weight")
    ax0.set_title("Step count heatmap")
    fig.colorbar(im, ax=ax0, label=z_label)

    min_idx = np.unravel_index(np.argmin(grid), grid.shape)
    ax0.scatter(margin_axis[min_idx[1]], progress_axis[min_idx[0]],
                marker="x", color="red", s=100, label="min")
    ax0.legend()

    # --- 3D surface ---
    ax1 = fig.add_subplot(1, 2, 2, projection="3d")
    mesh_margin, mesh_progress = np.meshgrid(margin_axis, progress_axis)
    surf = ax1.plot_surface(mesh_margin, mesh_progress, display_grid, cmap="viridis",
        linewidth=0, antialiased=True)
    ax1.set_xlabel("margin weight")
    ax1.set_ylabel("progress weight")
    ax1.set_zlabel(z_label)
    ax1.set_title("Step count surface")
    fig.colorbar(surf, ax=ax1, shrink=0.6, label=z_label)

    fig.tight_layout()
    plt.show()


if __name__ == "__main__":
    main()
