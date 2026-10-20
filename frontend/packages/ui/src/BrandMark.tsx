import markUrl from "./brand/workerdock-mark.png";

export function BrandMark({ size = 36 }: { size?: number }) {
  return (
    <img
      src={markUrl}
      alt=""
      aria-hidden="true"
      width={size}
      height={size}
      style={{ display: "block", flex: "none" }}
    />
  );
}
